# Cross toolchain for the compile-only target build (HAL=target). Proves the portable core
# compiles for a Cortex-M4 STM32 under arm-none-eabi-gcc. TRY_COMPILE as a static library so
# CMake's compiler check does not attempt to LINK (no startup/linker script/libc here — the
# whole point is compile-only, CLAUDE.md §3 "compiled but never run").
set(CMAKE_SYSTEM_NAME Generic)
set(CMAKE_SYSTEM_PROCESSOR arm)

set(CMAKE_C_COMPILER arm-none-eabi-gcc)
set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)

set(CMAKE_C_FLAGS_INIT "-mcpu=cortex-m4 -mthumb -ffreestanding")
