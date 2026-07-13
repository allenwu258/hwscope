cmake_minimum_required(VERSION 3.16)

project(HwScopeNativeSpd LANGUAGES CXX)

add_executable(spd
    src/main.cpp
)

target_compile_features(spd PRIVATE cxx_std_17)

if (MSVC)
    target_compile_options(spd PRIVATE /O2 /W4 /permissive-)
else()
    target_compile_options(spd PRIVATE -O3 -Wall -Wextra -pedantic)
endif()

if (WIN32)
    target_link_libraries(spd PRIVATE bcrypt)
endif()
