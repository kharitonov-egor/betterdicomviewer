cmake_minimum_required(VERSION 3.20)
project(bdv_vtk LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

find_package(VTK REQUIRED COMPONENTS
  CommonCore
  CommonDataModel
  CommonExecutionModel
  CommonMath
  CommonTransforms
  ImagingCore
  FiltersCore
  FiltersGeneral
  RenderingCore
  RenderingOpenGL2
  RenderingVolume
  RenderingVolumeOpenGL2
  IOGeometry
)

add_library(bdv_vtk SHARED src/bdv_vtk.cpp src/bdv_vtk.h)
target_compile_definitions(bdv_vtk PRIVATE BDV_VTK_EXPORTS NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(bdv_vtk PRIVATE ${VTK_LIBRARIES})
if(MSVC)
  target_compile_options(bdv_vtk PRIVATE /W3 /permissive- /utf-8)
endif()

vtk_module_autoinit(TARGETS bdv_vtk MODULES ${VTK_LIBRARIES})

install(TARGETS bdv_vtk RUNTIME DESTINATION .)
