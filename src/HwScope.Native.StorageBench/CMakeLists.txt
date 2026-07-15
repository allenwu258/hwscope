cmake_minimum_required(VERSION 3.16)

project(HwScopeNativeStorageBench LANGUAGES CXX)

add_executable(storagebench
    src/main.cpp
)

target_compile_features(storagebench PRIVATE cxx_std_17)

if (MSVC)
    target_compile_options(storagebench PRIVATE /O2 /W4 /permissive- /EHsc)
else()
    target_compile_options(storagebench PRIVATE -O3 -Wall -Wextra -pedantic)
endif()
