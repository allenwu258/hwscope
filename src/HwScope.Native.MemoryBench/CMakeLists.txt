cmake_minimum_required(VERSION 3.16)

project(HwScopeNativeMemoryBench LANGUAGES CXX)

add_executable(membench
    src/main.cpp
)

target_compile_features(membench PRIVATE cxx_std_17)

if (MSVC)
    target_compile_options(membench PRIVATE /O2 /W4 /permissive-)
else()
    target_compile_options(membench PRIVATE -O3 -Wall -Wextra -pedantic)
endif()
