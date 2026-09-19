/*
 * The header bindgen parses. One translation unit, the same includes driver.h uses for the C driver, so the
 * Rust driver sees exactly the declarations the C one does.
 *
 * Nothing here is compiled into the driver: bindgen reads this file with clang and writes Rust declarations
 * from it. The C compiler never sees it.
 */
#include <ntddk.h>
#include <wdf.h>
#include <hidport.h>
#include <vhf.h>
