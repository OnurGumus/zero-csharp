# The CUDA spike's tools: NVIDIA's ptxas (assembles PTX for a GPU, no GPU
# needed) and LLVM 18's llc/opt, whose NVPTX backend turns kernels into PTX.
FROM nvidia/cuda:12.6.3-devel-ubuntu24.04
RUN apt-get update && apt-get install -y --no-install-recommends llvm-18 && rm -rf /var/lib/apt/lists/*
ENV PATH=/usr/lib/llvm-18/bin:$PATH
