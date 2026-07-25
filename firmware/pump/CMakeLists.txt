cmake_minimum_required(VERSION 3.16)
project(ofp_pump C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

# CLAUDE.md §6 (C): -Wall -Wextra -Werror -pedantic, C11.
add_compile_options(-Wall -Wextra -Werror -pedantic)

# HAL selects the platform adapter the identical portable core binds to.
set(HAL "host" CACHE STRING "HAL implementation: host | target")

set(CORE_SOURCES
    src/crc16.c
    src/frame.c
    src/fsm.c
    src/pump.c)

add_library(ofp_core STATIC ${CORE_SOURCES})
target_include_directories(ofp_core PUBLIC src hal)

if(HAL STREQUAL "host")
    # Desktop/WSL2 build: UART over a TCP socket, real executable, unit tests.
    add_executable(pump_host hal/host/hal_host.c hal/host/main_host.c)
    target_link_libraries(pump_host PRIVATE ofp_core)
    # POSIX/glibc glue (clock_gettime, sockets, MSG_NOSIGNAL, nanosleep) lives only in the
    # host HAL — the portable core stays strict-C11/-pedantic with no platform defines.
    target_compile_definitions(pump_host PRIVATE _GNU_SOURCE)

    enable_testing()
    foreach(t crc frame fsm)
        add_executable(test_${t} test/test_${t}.c)
        target_link_libraries(test_${t} PRIVATE ofp_core)
        add_test(NAME test_${t} COMMAND test_${t})
    endforeach()

    add_executable(test_pump test/test_pump.c test/fake_hal.c)
    target_link_libraries(test_pump PRIVATE ofp_core)
    add_test(NAME test_pump COMMAND test_pump)

elseif(HAL STREQUAL "target")
    # Cross-compile proof only (CI, arm-none-eabi via cmake/arm-none-eabi.cmake). The core
    # plus the STM32 HAL adapter are archived compile-only — never linked or run here. This
    # is what proves the src/ core is genuinely portable, not host-coupled.
    add_library(pump_target STATIC hal/target/hal_target.c)
    target_link_libraries(pump_target PRIVATE ofp_core)

else()
    message(FATAL_ERROR "HAL must be 'host' or 'target' (got '${HAL}')")
endif()
