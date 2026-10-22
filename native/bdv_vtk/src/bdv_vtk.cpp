#include "bdv_vtk.h"

#include <vtkActor.h>
#include <vtkCamera.h>
#include <vtkColorTransferFunction.h>
#include <vtkDataArray.h>
#include <vtkDiscreteFlyingEdges3D.h>
#include <vtkGPUVolumeRayCastMapper.h>
#include <vtkImageData.h>
#include <vtkImageReslice.h>
#include <vtkMatrix4x4.h>
#include <vtkNew.h>
#include <vtkOBJWriter.h>
#include <vtkOpenGLRenderWindow.h>
#include <vtkOutputWindow.h>
#include <vtkPiecewiseFunction.h>
#include <vtkPlane.h>
#include <vtkPlaneCollection.h>
#include <vtkPointData.h>
#include <vtkPolyData.h>
#include <vtkPolyDataMapper.h>
#include <vtkPolyDataNormals.h>
#include <vtkProperty.h>
#include <vtkRenderWindow.h>
#include <vtkRenderer.h>
#include <vtkSMPTools.h>
#include <vtkSTLWriter.h>
#include <vtkSmartPointer.h>
#include <vtkThreadedImageAlgorithm.h>
#include <vtkTransform.h>
#include <vtkTransformFilter.h>
#include <vtkUnsignedCharArray.h>
#include <vtkVersion.h>
#include <vtkVolume.h>
#include <vtkVolumePicker.h>
#include <vtkVolumeProperty.h>
#include <vtkWindowedSincPolyDataFilter.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <exception>
#include <mutex>
#include <string>
#include <vector>

namespace
{
thread_local std::string g_lastError;
std::once_flag g_initOnce;

class SilentOutputWindow : public vtkOutputWindow
{
public:
  static SilentOutputWindow* New();
  vtkTypeMacro(SilentOutputWindow, vtkOutputWindow);

  void DisplayText(const char* text) override
  {
    if (text)
    {
      g_lastError = text;
    }
  }

protected:
  SilentOutputWindow() = default;
};
vtkStandardNewMacro(SilentOutputWindow);

void InitializeOnce(int threads)
{
  std::call_once(g_initOnce, [threads]() {
    vtkNew<SilentOutputWindow> window;
    vtkOutputWindow::SetInstance(window);
    vtkSMPTools::Initialize(threads > 0 ? threads : 0);
    vtkThreadedImageAlgorithm::SetGlobalDefaultEnableSMP(true);
  });
}

size_t ScalarSize(int scalarType)
{
  switch (scalarType)
  {
    case VTK_UNSIGNED_CHAR:
    case VTK_CHAR:
    case VTK_SIGNED_CHAR:
      return 1;
    case VTK_SHORT:
    case VTK_UNSIGNED_SHORT:
      return 2;
    case VTK_INT:
    case VTK_UNSIGNED_INT:
    case VTK_FLOAT:
      return 4;
    case VTK_DOUBLE:
      return 8;
    default:
      return 0;
  }
}

vtkSmartPointer<vtkMatrix4x4> MatrixFromRowMajor(const double* m)
{
  auto matrix = vtkSmartPointer<vtkMatrix4x4>::New();
  for (int r = 0; r < 4; ++r)
  {
    for (int c = 0; c < 4; ++c)
    {
      matrix->SetElement(r, c, m[r * 4 + c]);
    }
  }
  return matrix;
}

// Wraps caller-owned memory without copying. The caller keeps the buffer alive while VTK objects reference it.
struct WrappedImage
{
  vtkSmartPointer<vtkImageData> image;
  const void* data = nullptr;
  int dims[3] = { 0, 0, 0 };
  int components = 0;
  int scalarType = 0;
  long long version = -1;

  bool Update(const void* newData, int nx, int ny, int nz, int newComponents, int newScalarType, long long newVersion)
  {
    if (!newData || nx <= 0 || ny <= 0 || nz <= 0 || newComponents <= 0 || ScalarSize(newScalarType) == 0)
    {
      g_lastError = "Invalid image description.";
      return false;
    }

    const bool sameLayout = image && data == newData && dims[0] == nx && dims[1] == ny && dims[2] == nz &&
      components == newComponents && scalarType == newScalarType;
    if (!sameLayout)
    {
      auto array = vtkSmartPointer<vtkDataArray>::Take(vtkDataArray::CreateDataArray(newScalarType));
      array->SetNumberOfComponents(newComponents);
      array->SetVoidArray(const_cast<void*>(newData), static_cast<vtkIdType>(nx) * ny * nz * newComponents, 1);
      image = vtkSmartPointer<vtkImageData>::New();
      image->SetDimensions(nx, ny, nz);
      image->SetOrigin(0, 0, 0);
      image->SetSpacing(1, 1, 1);
      image->GetPointData()->SetScalars(array);
      data = newData;
      dims[0] = nx;
      dims[1] = ny;
      dims[2] = nz;
      components = newComponents;
      scalarType = newScalarType;
      version = newVersion;
    }
    else if (version != newVersion)
    {
      image->GetPointData()->GetScalars()->Modified();
      image->Modified();
      version = newVersion;
    }
    return true;
  }
};

struct Reslicer
{
  WrappedImage input;
  vtkSmartPointer<vtkImageReslice> reslice = vtkSmartPointer<vtkImageReslice>::New();
  vtkSmartPointer<vtkMatrix4x4> axes = vtkSmartPointer<vtkMatrix4x4>::New();

  Reslicer()
  {
    reslice->TransformInputSamplingOff();
    reslice->AutoCropOutputOff();
    reslice->SetOutputDimensionality(2);
    reslice->SetOutputSpacing(1, 1, 1);
    reslice->SetOutputOrigin(0, 0, 0);
    reslice->SetResliceAxes(axes);
    reslice->BorderOn();
  }
};

struct LabelSurfaceSet
{
  WrappedImage labels;
  std::vector<vtkSmartPointer<vtkActor>> actors;
  std::vector<double> rgba;
  int smoothIterations = -1;
  double matrix[16] = {};
};

vtkSmartPointer<vtkPolyData> ExtractLabelSurface(vtkImageData* labels, int label, int smoothIterations)
{
  vtkNew<vtkDiscreteFlyingEdges3D> edges;
  edges->SetInputData(labels);
  edges->SetValue(0, label);
  edges->ComputeNormalsOff();
  edges->ComputeGradientsOff();
  edges->ComputeScalarsOff();
  edges->Update();

  vtkSmartPointer<vtkPolyData> surface = edges->GetOutput();
  if (surface->GetNumberOfPoints() == 0)
  {
    return nullptr;
  }

  if (smoothIterations > 0)
  {
    vtkNew<vtkWindowedSincPolyDataFilter> smooth;
    smooth->SetInputData(surface);
    smooth->SetNumberOfIterations(smoothIterations);
    smooth->BoundarySmoothingOff();
    smooth->FeatureEdgeSmoothingOff();
    smooth->SetPassBand(0.05);
    smooth->NonManifoldSmoothingOn();
    smooth->NormalizeCoordinatesOn();
    smooth->Update();
    surface = smooth->GetOutput();
  }

  vtkNew<vtkPolyDataNormals> normals;
  normals->SetInputData(surface);
  normals->SplittingOff();
  normals->ConsistencyOn();
  normals->Update();
  vtkSmartPointer<vtkPolyData> result = normals->GetOutput();
  return result;
}

struct VolumeRenderer
{
  vtkSmartPointer<vtkRenderWindow> window = vtkSmartPointer<vtkRenderWindow>::New();
  vtkSmartPointer<vtkRenderer> renderer = vtkSmartPointer<vtkRenderer>::New();
  vtkSmartPointer<vtkGPUVolumeRayCastMapper> mapper = vtkSmartPointer<vtkGPUVolumeRayCastMapper>::New();
  vtkSmartPointer<vtkVolume> volume = vtkSmartPointer<vtkVolume>::New();
  vtkSmartPointer<vtkVolumeProperty> property = vtkSmartPointer<vtkVolumeProperty>::New();
  vtkSmartPointer<vtkColorTransferFunction> color = vtkSmartPointer<vtkColorTransferFunction>::New();
  vtkSmartPointer<vtkPiecewiseFunction> opacity = vtkSmartPointer<vtkPiecewiseFunction>::New();
  vtkSmartPointer<vtkPiecewiseFunction> gradient = vtkSmartPointer<vtkPiecewiseFunction>::New();
  vtkSmartPointer<vtkPlaneCollection> clipPlanes = vtkSmartPointer<vtkPlaneCollection>::New();
  WrappedImage input;
  LabelSurfaceSet labels;
  double sampleDistance = 0.5;
  bool hasVolume = false;
  std::string info;

  VolumeRenderer()
  {
    window->SetShowWindow(false);
    window->SetUseOffScreenBuffers(true);
    window->SetMultiSamples(0);
    window->AddRenderer(renderer);
    renderer->SetBackground(0, 0, 0);

    property->SetColor(color);
    property->SetScalarOpacity(opacity);
    property->SetInterpolationTypeToLinear();
    property->ShadeOn();
    volume->SetMapper(mapper);
    volume->SetProperty(property);
    mapper->SetAutoAdjustSampleDistances(0);
    mapper->SetUseJittering(1);
    mapper->SetClippingPlanes(clipPlanes);
    volume->VisibilityOff();
    renderer->AddVolume(volume);
  }
};

void CopyMatrix(const double* source, double* target)
{
  std::memcpy(target, source, sizeof(double) * 16);
}

bool SameMatrix(const double* a, const double* b)
{
  return std::memcmp(a, b, sizeof(double) * 16) == 0;
}
} // namespace

#define BDV_TRY try
#define BDV_CATCH                                                                                  \
  catch (const std::exception& ex)                                                                 \
  {                                                                                                \
    g_lastError = ex.what();                                                                       \
    return 0;                                                                                      \
  }                                                                                                \
  catch (...)                                                                                      \
  {                                                                                                \
    g_lastError = "Unknown native error.";                                                         \
    return 0;                                                                                      \
  }

BDV_API const char* bdv_version(void)
{
  static std::string version = std::string("bdv_vtk 1 / VTK ") + vtkVersion::GetVTKVersion();
  return version.c_str();
}

BDV_API const char* bdv_last_error(void)
{
  return g_lastError.c_str();
}

BDV_API void bdv_initialize(int threads)
{
  InitializeOnce(threads);
}

BDV_API void* bdv_reslicer_create(void)
{
  InitializeOnce(0);
  try
  {
    return new Reslicer();
  }
  catch (...)
  {
    g_lastError = "Could not create reslicer.";
    return nullptr;
  }
}

BDV_API void bdv_reslicer_destroy(void* reslicer)
{
  delete static_cast<Reslicer*>(reslicer);
}

BDV_API int bdv_reslice(void* reslicerPtr, const void* data, int nx, int ny, int nz, int components, int scalarType,
  long long dataVersion, const double* axesRowMajor16, int outWidth, int outHeight, int interpolation, int slabMode,
  int slabSlices, double background, void* out, int outScalarType)
{
  BDV_TRY
  {
    auto* r = static_cast<Reslicer*>(reslicerPtr);
    if (!r || !out || outWidth <= 0 || outHeight <= 0 || ScalarSize(outScalarType) == 0)
    {
      g_lastError = "Invalid reslice arguments.";
      return 0;
    }
    if (!r->input.Update(data, nx, ny, nz, components, scalarType, dataVersion))
    {
      return 0;
    }

    const bool slab = slabSlices > 1 && slabMode > 0;
    double m[16];
    for (int i = 0; i < 16; ++i)
    {
      // Round-off from the caller's geometry (around 1e-17) would keep VTK off its axis-aligned fast path.
      m[i] = std::abs(axesRowMajor16[i]) < 1e-10 ? 0.0 : axesRowMajor16[i];
    }
    bool permutation = true;
    for (int col = 0; col < 3 && permutation; ++col)
    {
      int nonZero = 0;
      for (int row = 0; row < 3; ++row)
      {
        nonZero += m[row * 4 + col] != 0.0 ? 1 : 0;
      }
      permutation = nonZero == 1;
    }
    // VTK 9.7's axis-aligned fast path centres slab samples at -0.5*n steps instead of -0.5*(n-1), half a step
    // off the plane. The general path is correct, so compensate only when the fast path will be taken.
    if (slab && permutation)
    {
      for (int row = 0; row < 3; ++row)
      {
        m[row * 4 + 3] += 0.5 * m[row * 4 + 2];
      }
    }
    for (int row = 0; row < 4; ++row)
    {
      for (int col = 0; col < 4; ++col)
      {
        r->axes->SetElement(row, col, m[row * 4 + col]);
      }
    }
    r->reslice->SetSlabSliceSpacingFraction(1.0);

    auto* reslice = r->reslice.GetPointer();
    reslice->SetInputData(r->input.image);
    reslice->SetResliceAxes(r->axes);
    reslice->SetOutputExtent(0, outWidth - 1, 0, outHeight - 1, 0, 0);
    reslice->SetOutputScalarType(outScalarType);
    reslice->SetBackgroundLevel(background);
    switch (interpolation)
    {
      case 0:
        reslice->SetInterpolationModeToNearestNeighbor();
        break;
      case 2:
        reslice->SetInterpolationModeToCubic();
        break;
      default:
        reslice->SetInterpolationModeToLinear();
        break;
    }
    if (slab)
    {
      reslice->SetSlabNumberOfSlices(slabSlices);
      switch (slabMode)
      {
        case 2:
          reslice->SetSlabModeToMax();
          break;
        case 3:
          reslice->SetSlabModeToMin();
          break;
        case 4:
          reslice->SetSlabModeToSum();
          break;
        default:
          reslice->SetSlabModeToMean();
          break;
      }
    }
    else
    {
      reslice->SetSlabNumberOfSlices(1);
    }

    reslice->Modified();
    reslice->Update();
    vtkImageData* output = reslice->GetOutput();
    const size_t bytes = static_cast<size_t>(outWidth) * outHeight * components * ScalarSize(outScalarType);
    const void* src = output->GetScalarPointer();
    if (!src)
    {
      g_lastError = "Reslice produced no output.";
      return 0;
    }
    std::memcpy(out, src, bytes);
    return 1;
  }
  BDV_CATCH
}

BDV_API void* bdv_vr_create(void)
{
  InitializeOnce(0);
  try
  {
    return new VolumeRenderer();
  }
  catch (...)
  {
    g_lastError = "Could not create volume renderer.";
    return nullptr;
  }
}

BDV_API void bdv_vr_destroy(void* vr)
{
  auto* renderer = static_cast<VolumeRenderer*>(vr);
  if (!renderer)
  {
    return;
  }
  renderer->window->Finalize();
  delete renderer;
}

BDV_API int bdv_vr_set_volume(void* vrPtr, const void* data, int nx, int ny, int nz, int scalarType, long long dataVersion,
  const double* indexToWorldRowMajor16)
{
  BDV_TRY
  {
    auto* vr = static_cast<VolumeRenderer*>(vrPtr);
    if (!vr->input.Update(data, nx, ny, nz, 1, scalarType, dataVersion))
    {
      return 0;
    }
    vr->mapper->SetInputData(vr->input.image);
    vr->volume->SetUserMatrix(MatrixFromRowMajor(indexToWorldRowMajor16));
    vr->volume->VisibilityOn();
    vr->hasVolume = true;
    return 1;
  }
  BDV_CATCH
}

BDV_API int bdv_vr_clear_volume(void* vrPtr)
{
  BDV_TRY
  {
    auto* vr = static_cast<VolumeRenderer*>(vrPtr);
    vr->volume->VisibilityOff();
    vr->mapper->RemoveAllInputs();
    vr->input = WrappedImage();
    vr->hasVolume = false;
    return 1;
  }
  BDV_CATCH
}

BDV_API int bdv_vr_set_transfer(void* vrPtr, const double* colorPoints, int colorCount, const double* opacityPoints,
  int opacityCount, const double* gradientPoints, int gradientCount, int blendMode, int shade, double ambient,
  double diffuse, double specular, double specularPower, double sampleDistance)
{
  BDV_TRY
  {
    auto* vr = static_cast<VolumeRenderer*>(vrPtr);
    vr->color->RemoveAllPoints();
    for (int i = 0; i < colorCount; ++i)
    {
      const double* p = colorPoints + i * 4;
      vr->color->AddRGBPoint(p[0], p[1], p[2], p[3]);
    }
    vr->opacity->RemoveAllPoints();
    for (int i = 0; i < opacityCount; ++i)
    {
      const double* p = opacityPoints + i * 2;
      vr->opacity->AddPoint(p[0], p[1]);
    }
    if (gradientCount > 0)
    {
      vr->gradient->RemoveAllPoints();
      for (int i = 0; i < gradientCount; ++i)
      {
        const double* p = gradientPoints + i * 2;
        vr->gradient->AddPoint(p[0], p[1]);
      }
      vr->property->SetGradientOpacity(vr->gradient);
      vr->property->DisableGradientOpacityOff();
    }
    else
    {
      vr->property->DisableGradientOpacityOn();
    }

    // Jittering reduces wood-grain artefacts in composite rendering but leaves speckles in projections.
    vr->mapper->SetUseJittering(blendMode == 0 ? 1 : 0);
    switch (blendMode)
    {
      case 1:
        vr->mapper->SetBlendModeToMaximumIntensity();
        break;
      case 2:
        vr->mapper->SetBlendModeToMinimumIntensity();
        break;
      case 3:
        vr->mapper->SetBlendModeToAverageIntensity();
        break;
      default:
        vr->mapper->SetBlendModeToComposite();
        break;
    }
    vr->property->SetShade(shade ? 1 : 0);
    vr->property->SetAmbient(ambient);
    vr->property->SetDiffuse(diffuse);
    vr->property->SetSpecular(specular);
    vr->property->SetSpecularPower(specularPower);
    vr->sampleDistance = sampleDistance > 0 ? sampleDistance : 0.5;
    return 1;
  }
  BDV_CATCH
}

BDV_API int bdv_vr_set_camera(void* vrPtr, const double* position, const double* focalPoint, const double* viewUp,
  double viewAngle, int parallel, double parallelScale)
{
  BDV_TRY
  {
    auto* vr = static_cast<VolumeRenderer*>(vrPtr);
    vtkCamera* camera = vr->renderer->GetActiveCamera();
    camera->SetPosition(position);
    camera->SetFocalPoint(focalPoint);
    camera->SetViewUp(viewUp);
    camera->SetViewAngle(viewAngle);
    camera->SetParallelProjection(parallel ? 1 : 0);
    camera->SetParallelScale(parallelScale);
    vr->renderer->ResetCameraClippingRange();
    return 1;
  }
  BDV_CATCH
}

BDV_API int bdv_vr_set_clip_planes(void* vrPtr, const double* planes, int planeCount)
{
  BDV_TRY
  {
    auto* vr = static_cast<VolumeRenderer*>(vrPtr);
    vr->clipPlanes->RemoveAllItems();
    for (int i = 0; i < planeCount; ++i)
    {
      const double* p = planes + i * 6;
      vtkNew<vtkPlane> plane;
      plane->SetOrigin(p[0], p[1], p[2]);
      plane->SetNormal(p[3], p[4], p[5]);
      vr->clipPlanes->AddItem(plane);
    }
    vr->mapper->SetClippingPlanes(vr->clipPlanes);
    for (auto& actor : vr->labels.actors)
    {
      actor->GetMapper()->SetClippingPlanes(vr->clipPlanes);
    }
    return 1;
  }
  BDV_CATCH
}

BDV_API int bdv_vr_set_background(void* vrPtr, double r, double g, double b)
{
  BDV_TRY
  {
    static_cast<VolumeRenderer*>(vrPtr)->renderer->SetBackground(r, g, b);
    return 1;
  }
  BDV_CATCH
}

BDV_API int bdv_vr_set_labels(void* vrPtr, const unsigned char* labels, int nx, int ny, int nz, long long version,
  const double* indexToWorldRowMajor16, const double* rgba, int labelCount, int smoothIterations)
{
  BDV_TRY
  {
    auto* vr = static_cast<VolumeRenderer*>(vrPtr);
    auto& set = vr->labels;

    if (!labels || labelCount <= 0)
    {
      for (auto& actor : set.actors)
      {
        vr->renderer->RemoveActor(actor);
      }
      set.actors.clear();
      set.labels = WrappedImage();
      return 1;
    }

    std::vector<double> newRgba(rgba, rgba + labelCount * 4);
    const bool unchanged = set.labels.image && set.labels.data == labels && set.labels.version == version &&
      set.rgba == newRgba && set.smoothIterations == smoothIterations && SameMatrix(set.matrix, indexToWorldRowMajor16);
    if (unchanged)
    {
      return 1;
    }

    if (!set.labels.Update(labels, nx, ny, nz, 1, VTK_UNSIGNED_CHAR, version))
    {
      return 0;
    }
    set.rgba = newRgba;
    set.smoothIterations = smoothIterations;
    CopyMatrix(indexToWorldRowMajor16, set.matrix);

    for (auto& actor : set.actors)
    {
      vr->renderer->RemoveActor(actor);
    }
    set.actors.clear();

    std::vector<bool> present(256, false);
    const size_t count = static_cast<size_t>(nx) * ny * nz;
    for (size_t i = 0; i < count; ++i)
    {
      present[labels[i]] = true;
    }

    auto matrix = MatrixFromRowMajor(indexToWorldRowMajor16);
    for (int label = 1; label <= labelCount && label < 256; ++label)
    {
      const double* c = rgba + (label - 1) * 4;
      if (!present[label] || c[3] <= 0)
      {
        continue;
      }
      auto surface = ExtractLabelSurface(set.labels.image, label, smoothIterations);
      if (!surface)
      {
        continue;
      }
      vtkNew<vtkPolyDataMapper> mapper;
      mapper->SetInputData(surface);
      mapper->ScalarVisibilityOff();
      mapper->SetClippingPlanes(vr->clipPlanes);
      auto actor = vtkSmartPointer<vtkActor>::New();
      actor->SetMapper(mapper);
      actor->SetUserMatrix(matrix);
      actor->GetProperty()->SetColor(c[0], c[1], c[2]);
      actor->GetProperty()->SetOpacity(c[3]);
      actor->GetProperty()->SetSpecular(0.2);
      actor->GetProperty()->SetSpecularPower(20);
      vr->renderer->AddActor(actor);
      set.actors.push_back(actor);
    }
    return 1;
  }
  BDV_CATCH
}

BDV_API int bdv_vr_render(void* vrPtr, int width, int height, int interactive, void* outBgra)
{
  BDV_TRY
  {
    auto* vr = static_cast<VolumeRenderer*>(vrPtr);
    if (width <= 0 || height <= 0 || !outBgra)
    {
      g_lastError = "Invalid render size.";
      return 0;
    }
    vr->window->SetSize(width, height);
    vr->mapper->SetSampleDistance(interactive ? vr->sampleDistance * 2.0 : vr->sampleDistance);
    vr->mapper->SetImageSampleDistance(interactive ? 1.6 : 1.0);
    vr->renderer->ResetCameraClippingRange();
    vr->window->Render();

    if (vr->info.empty())
    {
      if (auto* gl = vtkOpenGLRenderWindow::SafeDownCast(vr->window))
      {
        vr->info = gl->ReportCapabilities();
      }
    }

    vtkNew<vtkUnsignedCharArray> pixels;
    if (vr->window->GetRGBACharPixelData(0, 0, width - 1, height - 1, 0, pixels) != VTK_OK ||
      pixels->GetNumberOfTuples() < static_cast<vtkIdType>(width) * height)
    {
      g_lastError = "Could not read rendered pixels.";
      return 0;
    }

    const unsigned char* src = pixels->GetPointer(0);
    auto* dst = static_cast<unsigned char*>(outBgra);
    for (int y = 0; y < height; ++y)
    {
      const unsigned char* srcRow = src + static_cast<size_t>(height - 1 - y) * width * 4;
      unsigned char* dstRow = dst + static_cast<size_t>(y) * width * 4;
      for (int x = 0; x < width; ++x)
      {
        dstRow[x * 4 + 0] = srcRow[x * 4 + 2];
        dstRow[x * 4 + 1] = srcRow[x * 4 + 1];
        dstRow[x * 4 + 2] = srcRow[x * 4 + 0];
        dstRow[x * 4 + 3] = 255;
      }
    }
    return 1;
  }
  BDV_CATCH
}

BDV_API int bdv_vr_pick(void* vrPtr, int x, int y, int width, int height, double* outWorld3)
{
  BDV_TRY
  {
    auto* vr = static_cast<VolumeRenderer*>(vrPtr);
    if (!vr->hasVolume)
    {
      g_lastError = "No volume loaded.";
      return 0;
    }
    vr->window->SetSize(width, height);
    vtkNew<vtkVolumePicker> picker;
    picker->SetTolerance(1e-6);
    picker->SetVolumeOpacityIsovalue(0.15);
    picker->PickCroppingPlanesOff();
    picker->AddPickList(vr->volume);
    for (auto& actor : vr->labels.actors)
    {
      picker->AddPickList(actor);
    }
    picker->PickFromListOn();
    if (!picker->Pick(x, height - 1 - y, 0, vr->renderer))
    {
      g_lastError = "Nothing under the cursor.";
      return 0;
    }
    picker->GetPickPosition(outWorld3);
    return 1;
  }
  BDV_CATCH
}

BDV_API const char* bdv_vr_renderer_info(void* vrPtr)
{
  auto* vr = static_cast<VolumeRenderer*>(vrPtr);
  return vr ? vr->info.c_str() : "";
}

BDV_API int bdv_export_label_surface(const unsigned char* labels, int nx, int ny, int nz,
  const double* indexToWorldRowMajor16, int label, int smoothIterations, const char* pathUtf8, int format)
{
  BDV_TRY
  {
    InitializeOnce(0);
    WrappedImage image;
    if (!image.Update(labels, nx, ny, nz, 1, VTK_UNSIGNED_CHAR, 0))
    {
      return 0;
    }
    auto surface = ExtractLabelSurface(image.image, label, smoothIterations);
    if (!surface)
    {
      g_lastError = "The segment is empty.";
      return 0;
    }

    vtkNew<vtkTransform> transform;
    transform->SetMatrix(MatrixFromRowMajor(indexToWorldRowMajor16));
    vtkNew<vtkTransformFilter> toWorld;
    toWorld->SetInputData(surface);
    toWorld->SetTransform(transform);
    toWorld->Update();

    if (format == 1)
    {
      vtkNew<vtkOBJWriter> writer;
      writer->SetFileName(pathUtf8);
      writer->SetInputData(toWorld->GetPolyDataOutput());
      if (!writer->Write())
      {
        g_lastError = "OBJ export failed.";
        return 0;
      }
    }
    else
    {
      vtkNew<vtkSTLWriter> writer;
      writer->SetFileName(pathUtf8);
      writer->SetFileTypeToBinary();
      writer->SetHeader("Better DICOM Viewer segment surface; DICOM patient LPS coordinates in mm");
      writer->SetInputData(toWorld->GetPolyDataOutput());
      if (!writer->Write())
      {
        g_lastError = "STL export failed.";
        return 0;
      }
    }
    return 1;
  }
  BDV_CATCH
}
