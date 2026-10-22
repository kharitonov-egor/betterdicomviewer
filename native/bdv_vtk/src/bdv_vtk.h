#pragma once

#ifdef BDV_VTK_EXPORTS
#define BDV_API extern "C" __declspec(dllexport)
#else
#define BDV_API extern "C" __declspec(dllimport)
#endif

// Scalar type codes match VTK: 3 = uint8, 4 = int16, 5 = uint16, 6 = int32, 10 = float32.
// Volumes are passed in index space (origin 0, spacing 1). All patient geometry is computed by the caller,
// so a reslice matrix maps output pixel indices directly to input voxel indices.
// Functions return 1 on success and 0 on failure; bdv_last_error() describes the failure on the calling thread.

BDV_API const char* bdv_version(void);
BDV_API const char* bdv_last_error(void);
BDV_API void bdv_initialize(int threads);

BDV_API void* bdv_reslicer_create(void);
BDV_API void bdv_reslicer_destroy(void* reslicer);
BDV_API int bdv_reslice(void* reslicer,
                        const void* data, int nx, int ny, int nz, int components, int scalarType, long long dataVersion,
                        const double* axesRowMajor16,
                        int outWidth, int outHeight,
                        int interpolation, int slabMode, int slabSlices,
                        double background,
                        void* out, int outScalarType);

BDV_API void* bdv_vr_create(void);
BDV_API void bdv_vr_destroy(void* vr);
BDV_API int bdv_vr_set_volume(void* vr, const void* data, int nx, int ny, int nz, int scalarType, long long dataVersion,
                              const double* indexToWorldRowMajor16);
BDV_API int bdv_vr_clear_volume(void* vr);
BDV_API int bdv_vr_set_transfer(void* vr,
                                const double* colorPoints, int colorCount,
                                const double* opacityPoints, int opacityCount,
                                const double* gradientPoints, int gradientCount,
                                int blendMode, int shade,
                                double ambient, double diffuse, double specular, double specularPower,
                                double sampleDistance);
BDV_API int bdv_vr_set_camera(void* vr, const double* position, const double* focalPoint, const double* viewUp,
                              double viewAngle, int parallel, double parallelScale);
BDV_API int bdv_vr_set_clip_planes(void* vr, const double* planes, int planeCount);
BDV_API int bdv_vr_set_background(void* vr, double r, double g, double b);
BDV_API int bdv_vr_set_labels(void* vr, const unsigned char* labels, int nx, int ny, int nz, long long version,
                              const double* indexToWorldRowMajor16, const double* rgba, int labelCount, int smoothIterations);
BDV_API int bdv_vr_render(void* vr, int width, int height, int interactive, void* outBgra);
BDV_API int bdv_vr_pick(void* vr, int x, int y, int width, int height, double* outWorld3);
BDV_API const char* bdv_vr_renderer_info(void* vr);

BDV_API int bdv_export_label_surface(const unsigned char* labels, int nx, int ny, int nz,
                                     const double* indexToWorldRowMajor16, int label, int smoothIterations,
                                     const char* pathUtf8, int format);
