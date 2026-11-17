// burn.cu: keep one GPU busy with FMA loops for N seconds (slot-sensor test). Usage: burn <cuda index> <seconds>
#include <cstdio>
#include <cstdlib>
#include <chrono>
__global__ void k(float *o, int iters) {
  float a = threadIdx.x * 1e-3f, b = 1.0001f, c = 0.9999f;
  for (int i = 0; i < iters; i++) { a = fmaf(a, b, c); b = fmaf(b, c, a * 1e-9f); }
  o[blockIdx.x * blockDim.x + threadIdx.x] = a + b;
}
int main(int argc, char **argv) {
  if (argc < 3) { fprintf(stderr, "usage: burn <cuda index> <seconds>\n"); return 2; }
  cudaSetDevice(atoi(argv[1]));
  int sms; cudaDeviceGetAttribute(&sms, cudaDevAttrMultiProcessorCount, atoi(argv[1]));
  int blocks = sms * 16; float *o; cudaMalloc(&o, blocks * 256 * sizeof(float));
  auto end = std::chrono::steady_clock::now() + std::chrono::seconds(atoi(argv[2]));
  while (std::chrono::steady_clock::now() < end) { k<<<blocks, 256>>>(o, 1 << 16); cudaDeviceSynchronize(); }
  cudaError_t e = cudaGetLastError(); if (e) { fprintf(stderr, "%s\n", cudaGetErrorString(e)); return 1; }
  return 0;
}
