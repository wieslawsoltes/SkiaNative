cmake_minimum_required(VERSION 3.25)
project(SkiaNativeAvalonia LANGUAGES CXX)

option(SKIANATIVE_WITH_SKIA "Build with Skia headers and library" ON)
set(SKIANATIVE_SKIA_ROOT "${CMAKE_CURRENT_LIST_DIR}/../../external/skia" CACHE PATH "Skia source root")
set(SKIANATIVE_SKIA_OUT "${SKIANATIVE_SKIA_ROOT}/out/${CMAKE_OSX_ARCHITECTURES}" CACHE PATH "Skia GN output directory")

add_library(SkiaNativeAvalonia SHARED
    src/SkiaNativeApi.cpp)

target_include_directories(SkiaNativeAvalonia
    PUBLIC include)

target_compile_features(SkiaNativeAvalonia PRIVATE cxx_std_20)
target_compile_definitions(SkiaNativeAvalonia PRIVATE $<$<BOOL:${SKIANATIVE_WITH_SKIA}>:SKIANATIVE_WITH_SKIA=1>)
target_compile_options(SkiaNativeAvalonia PRIVATE
    $<$<AND:$<CXX_COMPILER_ID:AppleClang,Clang>,$<CONFIG:Release>>:-O3;-DNDEBUG>)

include(CheckIPOSupported)
check_ipo_supported(RESULT SKIANATIVE_IPO_SUPPORTED OUTPUT SKIANATIVE_IPO_OUTPUT)
if(SKIANATIVE_IPO_SUPPORTED)
    set_property(TARGET SkiaNativeAvalonia PROPERTY INTERPROCEDURAL_OPTIMIZATION_RELEASE TRUE)
endif()

if(SKIANATIVE_WITH_SKIA)
    target_include_directories(SkiaNativeAvalonia PRIVATE "${SKIANATIVE_SKIA_ROOT}")
    if(APPLE)
        target_link_options(SkiaNativeAvalonia PRIVATE "-Wl,-force_load,${SKIANATIVE_SKIA_OUT}/libskia.a")
    elseif(UNIX)
        target_link_options(SkiaNativeAvalonia PRIVATE "-Wl,--whole-archive" "${SKIANATIVE_SKIA_OUT}/libskia.a" "-Wl,--no-whole-archive")
    elseif(MSVC)
        target_link_options(SkiaNativeAvalonia PRIVATE "/WHOLEARCHIVE:${SKIANATIVE_SKIA_OUT}/libskia.lib")
    else()
        target_link_libraries(SkiaNativeAvalonia PRIVATE "${SKIANATIVE_SKIA_OUT}/libskia.a")
    endif()
    file(GLOB SKIANATIVE_SKIA_PATHOPS_OBJECTS "${SKIANATIVE_SKIA_OUT}/obj/src/pathops/*.o")
    target_link_libraries(SkiaNativeAvalonia PRIVATE ${SKIANATIVE_SKIA_PATHOPS_OBJECTS})
endif()

if(APPLE)
    target_link_libraries(SkiaNativeAvalonia PRIVATE
        "-framework Foundation"
        "-framework CoreFoundation"
        "-framework CoreGraphics"
        "-framework CoreServices"
        "-framework CoreText"
        "-framework Metal"
        "-framework QuartzCore")
endif()

set_target_properties(SkiaNativeAvalonia PROPERTIES
    OUTPUT_NAME "SkiaNativeAvalonia"
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES)
