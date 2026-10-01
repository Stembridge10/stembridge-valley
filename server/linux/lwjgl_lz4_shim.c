/* Stardew calls LWJGL's LZ4 JNI symbols for network compression. This re-exports them over system liblz4 (arm64). */
#include <lz4.h>
int Java_org_lwjgl_util_lz4_LZ4_LZ4_1compressBound(void* env, void* clazz, int n) { return LZ4_compressBound(n); }
int Java_org_lwjgl_util_lz4_LZ4_nLZ4_1compress_1default(void* env, void* clazz, const char* src, char* dst, int srcSize, int cap) { return LZ4_compress_default(src, dst, srcSize, cap); }
int Java_org_lwjgl_util_lz4_LZ4_nLZ4_1decompress_1safe(void* env, void* clazz, const char* src, char* dst, int cSize, int cap) { return LZ4_decompress_safe(src, dst, cSize, cap); }
