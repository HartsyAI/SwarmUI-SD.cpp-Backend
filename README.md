# SwarmUI StableDiffusion.cpp Backend
===========================================================================

![SD.cpp Backend](url_to_image_placeholder)

## Table of Contents
-----------------

1. [Introduction](#introduction)
2. [Screenshots (TODO)](#screenshots-todo)
3. [Features](#features)
4. [Quick Start](#quick-start)
5. [Configuration](#configuration)
6. [Usage Tips](#usage-tips)
7. [Performance & Caching (TODO)](#performance--caching-todo)
8. [Troubleshooting](#troubleshooting)
9. [Architecture Support](#architecture-support)
10. [Advanced Features](#advanced-features)
11. [Technical Architecture](#technical-architecture)
12. [Performance Benchmarks](#performance-benchmarks)
13. [Contributing](#contributing)
14. [License](#license)
15. [Credits](#credits)

## Introduction
---------------

This extension adds a SwarmUI backend powered by [stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp). It runs image generation through an external SD.cpp executable (CPU/CUDA/Vulkan) and integrates the results into SwarmUI.

## Screenshots (TODO)
-------------------

- **TODO**: Add a screenshot of `Server → Extensions` showing the SD.cpp Backend install/enable button.
- **TODO**: Add a screenshot of `Server → Backends → SD.cpp Backend` settings (device selection, CUDA version, auto-update).
- **TODO**: Add a screenshot of the Text-to-Image page using the SD.cpp backend with the SD.cpp parameter groups.
- **TODO**: Add a screenshot showing live preview output (TAESD) during generation.

## Features
------------

### Core capabilities
- **Z-Image Models** - Supports Z-Image Turbo with the required Qwen LLM text encoder.
- **Flux Models** - Full support for FLUX.1-dev, FLUX.1-schnell, and FLUX.2-dev with automatic component management.
- **SD3/SD3.5** - Multi-component architecture support (CLIP-G, CLIP-L, T5-XXL) for SD3 family models.
- **SDXL/SD1.5/SD2** - Compatible with the mainstream Stable Diffusion architectures.
- **Video Generation** - Wan 2.1/2.2 models provide text-to-video and image-to-video modes.
- **GGUF Format** - Load quantized GGUF models in common precisions (Q2_K, Q4_K, Q8_0).
- **LoRA Support** - Automatic LoRA discovery from the Models/Lora directory.
- **ControlNet (experimental)** - Single ControlNet per job with detection of unsupported setups.
- **Live Previews** - TAESD previews update frequently during generation. TODO: This needs to be tested.
- **Auto-Update** - Automatically downloads SD.cpp binaries from GitHub releases when enabled.

### Performance optimizations
TODO: This needs work. Currently generations are slow even on repeat runs.
- **Inference Caching** - cache-dit/ucache/easycache integration exists but is currently not delivering the expected speedups.
- **Memory Mapping** - The `--mmap` option speeds up model loading and reduces RAM usage.
- **VAE Convolution** - Direct convolution (`--vae-conv-direct`) accelerates decoding.
- **VAE Tiling** - Breaks down VAE work into tiles to lower VRAM requirements.
- **CPU Offloading** - Move the VAE and CLIP encoders to CPU when GPU memory is constrained.
- **Flash Attention** - Optional attention path that saves memory at slight quality cost.

### Platform support
- **CUDA** - NVIDIA GPUs using the installed CUDA toolkits (11.x, 12.x, or compatible 13.x drivers).
- **CPU** - AVX/AVX2-compatible fallback.
- **Vulkan** - Experimental GPU acceleration path that can work on non-NVIDIA hardware (limited Flux support).

## Quick Start
--------------

### Installation

This extension is installed like any other SwarmUI extension.

### Preferred Method (Via SwarmUI)

1. Open your SwarmUI instance.
2. Navigate to `Server → Extensions`.
3. Find "SD.cpp Backend".
4. Click Install.
5. Restart SwarmUI when prompted (extensions require a rebuild/restart to load).

### Manual Installation

1. Close SwarmUI.
2. Clone this repository into `SwarmUI/src/Extensions/SwarmUI-SD.cpp-Backend/`.
3. Restart SwarmUI.
4. Go to `Server → Extensions` and enable "SD.cpp Backend".

### First Run

1. Open `Server → Backends → SD.cpp Backend`.
2. Choose your device (CPU, CUDA, or Vulkan).
3. (CUDA only) Leave CUDA version on Auto unless you know you need 11.x vs 12.x.
4. The installer downloads the SD.cpp release into `dlbackend/sdcpp/{device}` and then creates a `run-sd-server.sh` wrapper on Linux that always sets `LD_LIBRARY_PATH` to the binary directory before launching, so the bundled shared libraries (e.g. `libstable-diffusion.so`) are resolvable even on clean systems.

### Add Models

Place your models in `Models/Stable-Diffusion/`. GGUF models go into the `/diffusion_models/` folder. 

Supported formats include:
- GGUF models
- SafeTensors
- CKPT/BIN

Follow the [SwarmUI Supported Models documentation](https://github.com/kalebbroo/SwarmUI/blob/master/docs/Model%20Support.md ) for more details on properly installing models into Swarm.

## Configuration
-------------

### Backend Settings

Access settings via `Server → Backends → SD.cpp Backend`

**Device Selection:**
- `CPU` - Universal, works on any system (slower)
- `CUDA` - NVIDIA GPUs (best performance)
- `Vulkan` - Any modern GPU (experimental for Flux)

**CUDA Version** (Auto-detects):
- `Auto` - Automatically detects your CUDA installation (recommended)
- `CUDA 11.x` - For older NVIDIA drivers (450+)
- `CUDA 12.x` - For newer NVIDIA drivers (525+)
- `CUDA 13.x` - Uses CUDA 12 binaries (forward compatible)

**Auto-Update:**
- Enabled by default
- Checks GitHub for SD.cpp updates on startup
- Downloads latest version automatically

### Performance Parameters

**SD.cpp Performance Group** (Advanced Settings):

| Parameter | Default | Description |
|-----------|---------|-------------|
| Memory Map Models | `true` | Faster loading, reduced RAM usage |
| VAE Direct Convolution | `true` | Significantly faster VAE decoding |
| Cache Mode | `auto` | Inference caching (cache-dit for Flux, ucache for SD/SDXL) |
| Cache Preset | `fast` | Cache quality/speed (slow/medium/fast/ultra) |

**Cache Modes:**
- `auto` - Automatically selects best mode (recommended)
- `cache-dit` - For Flux/DiT models (5-10x speedup)
- `ucache` - For SD/SDXL/UNET models
- `easycache` - Simple condition-level cache
- `none` - Disable caching

**Cache Presets** (only with cache-dit):
- `ultra` - Maximum speed, slight quality trade-off
- `fast` - Balanced speed/quality (recommended)
- `medium` - Better quality, moderate speedup
- `slow` - Best quality, minimal speedup

**SD.cpp Group** (Standard Settings):

| Parameter | Default | Description |
|-----------|---------|-------------|
| VAE Tiling | `true` | Reduce VRAM usage (recommended for <12GB VRAM) |
| VAE on CPU | `false` | Offload VAE to CPU (if running out of VRAM) |
| CLIP on CPU | `false` | Offload CLIP to CPU (if running out of VRAM) |
| Flash Attention | `false` | Memory-efficient attention (may reduce quality slightly) |
| SD.cpp Sampler | `euler` | Sampling method (euler for Flux, euler_a for SD/SDXL) |
| SD.cpp Scheduler | `(empty)` | Scheduler type (discrete, karras, exponential, ays, gits) |
| TAESD Preview Decoder | `(None)` | Fast preview decoder for live previews |
| ESRGAN Upscale Model | `(None)` | Post-processing upscaler |
| Upscale Repeats | `1` | Number of upscale passes |
| Color Projection | `false` | Color correction for img2img consistency |

## Usage Tips
----------

## Known Issues
----------------

- **Slow generations** – Initial caching is slow and repeated runs do not yet reach expected speedups across architectures.
- **Z-Image text encoder** – SD.cpp fails to load the shipped Qwen text encoder (`text_encoders.llm.model.*` tensors are missing), so Z-Image inference currently errors unless you manually provide a compatible encoder (GGUF version is confirmed to work).
- **Previews** – TAESD preview images frequently fail to render because the SD.cpp binary reports `--preview` as unsupported on some builds; expect missing preview frames.
- **Img2Img/Upscaling** – The backend has not been fully exercised with img2img or upscaling workflows, so their behavior remains unverified and may have undiscovered issues.

## Performance & Caching (TODO)
------------------------------

- **TODO**: The current caching/performance behavior is not acceptable. Identify why repeat generations are not speeding up as expected.
- **TODO**: Verify and document when `cache-dit`, `ucache`, and `easycache` actually apply, and what models/architectures benefit.
- **TODO**: Add profiling notes (CPU vs CUDA vs Vulkan), common bottlenecks, and recommended defaults.
- **TODO**: Add a small troubleshooting matrix for "first run slow" vs "every run slow".

### Performance Optimization

**For Best Speed:**
1. Enable "Cache Mode" set to `auto` or `cache-dit`
2. Set "Cache Preset" to `ultra` or `fast`
3. Enable "Memory Map Models"
4. Enable "VAE Direct Convolution"
5. Use quantized GGUF models (Q4_K or Q8_0)
6. First generation builds cache (slow), subsequent generations are 5-10x faster

**For Limited VRAM:**
1. Enable "VAE Tiling"
2. Enable "VAE on CPU" and "CLIP on CPU"
3. Use lower quantization (Q2_K, Q4_0)
4. Reduce image dimensions
5. Close other GPU applications

### Dynamic VRAM Policy (Auto Offload)

SwarmUI automatically applies SD.cpp offload flags when VRAM is tight. This system is always on and uses real
model file sizes, GPU free VRAM, and generation parameters (resolution + batch count) to decide which flags are
needed. It does **not** clear VRAM between generations, so models stay resident and repeat runs stay fast.

**How it works:**
- Computes an estimated VRAM footprint from model sizes + runtime overhead + resolution
- Compares that to free VRAM with a safety margin
- Gradually escalates offload flags only if required

**Escalation order (more aggressive as needed):**
1. `--vae-tiling`
2. `--clip-on-cpu`
3. `--vae-on-cpu`
4. `--offload-to-cpu`

**Notes:**
- User-set flags are respected if they are *more aggressive* than the auto-policy.
- Very low VRAM GPUs (<6 GB) will automatically enable all offload flags.

### LoRA Usage

- **TODO**: Test and verify LoRA functionality with various models.

**Important:**
- Place LoRA files in `Models/Lora/`
- Backend automatically detects LoRA directory

### Video Generation (Wan Models)

Video-specific parameters:
- **Video Frames**: Number of frames to generate
- **Video FPS**: Frames per second
- **Flow Shift**: Flow control (default 3.0 for Wan)
- **Wan 2.2**: Supports dual-model system with high-noise diffusion model

## Troubleshooting
---------------
### No Preview Images

**Problem:** Live previews not showing during generation
- **TODO**: This is still a work in progress and needs testing.

**Solution:**
- Backend automatically enables TAESD previews
- Previews require SD.cpp `master-1896b28` or newer
- Check logs to verify `--preview tae` is being used
- Previews update every step or every 500ms

### Out of Memory

**Problem:** CUDA out of memory or allocation failed

**Solution:**
1. Enable "VAE Tiling"
2. Enable "VAE on CPU"
3. Use lower quantization (Q2_K instead of Q8_0)
4. Reduce image dimensions
5. Close other GPU applications

## Architecture Support
--------------------

| Model Type | Status | Notes |
|------------|--------|-------|
| **Z-Image** | | |
| Z-Image | Full | Uses Qwen LLM, auto-downloads components |
| **Flux** | | |
| FLUX.1-dev | Full | Requires CLIP-L + T5-XXL + VAE components |
| FLUX.1-schnell | Full | Distilled 4-step variant |
| FLUX.1-Kontext-dev | Full | Image edit model (uses input image) |
| FLUX.2-dev | Full | Latest Flux architecture |
| **Chroma** | | |
| Chroma | Full | Flux-based distilled model |
| Chroma1-Radiance | Full | Flux-based distilled model |
| **Ovis** | | |
| Ovis-Image | Full | Flux-based multimodal model |
| **Qwen Image** | | |
| Qwen Image | Full | Uses Qwen LLM component |
| Qwen Image Edit | Full | Image edit model (uses input image + Qwen LLM) |
| **SD3** | | |
| SD3 | Full | CLIP-G + CLIP-L + T5-XXL components |
| SD3.5 | Full | CLIP-G + CLIP-L + T5-XXL components |
| **SDXL** | | |
| SDXL Base | Full | Standard safetensors/ckpt |
| SDXL Turbo | Full | 4-8 step variant |
| SDXL Lightning | Full | Fast inference variant |
| **SD 1.x/2.x** | | |
| SD 1.5 | Full | 512x512 resolution |
| SD 1.5 Turbo | Full | Fast variant |
| SD 2.x | Full | 768x768 resolution |
| **LCM** | | |
| LCM Models | Full | 2-8 step inference |
| **Video** | | |
| Wan 2.1 | Full | Text-to-video, image-to-video |
| Wan 2.2 | Full | Dual-model system |

## Advanced Features
-----------------

### Img2Img and Inpainting
- **TODO**: Test and verify img2img and inpainting functionality.

- **Init Image**: Automatic img2img support
- **Init Image Creativity**: Strength parameter (0.0-1.0)
- **Mask Image**: Inpainting with mask support

### ControlNet (Experimental)

- SD.cpp supports single ControlNet
- Backend will warn if multiple ControlNets used
- ControlNet model + control image required
- Control strength parameter (0.0-2.0)

### Batch Generation
- **TODO**: Test and verify batch generation functionality.

- Batch count parameter (generates multiple images)
- SD.cpp `--batch-count` flag
- All images saved and returned

### ESRGAN Upscaling
- **TODO**: Test and verify ESRGAN upscaling functionality.

- Post-processing upscaler
- Supports RealESRGAN models
- Multiple upscale passes supported
- Place upscale models in `Models/upscale_model/`

## Technical Architecture
----------------------

### Extension Structure

```
SwarmUI-SD.cpp-Backend/
├── SDcppExtension.cs              # Main extension entry point
├── SwarmBackends/
│   └── SDcppBackend.cs            # Backend implementation (~400 lines, refactored)
├── Models/
│   ├── SDcppModelManager.cs       # Model detection, validation, downloads
│   └── SDcppParameterBuilder.cs   # Parameter conversion to SD.cpp CLI format
├── Utils/
│   ├── GGUFConverter.cs           # GGUF conversion helpers
│   ├── SDcppDownloadManager.cs    # Auto-download SD.cpp binaries
│   ├── SDcppProcessManager.cs     # Process execution and output capture
│   └── SDcppVramPolicy.cs         # Dynamic VRAM offload policy
└── WebAPI/
    └── SDcppAPI.cs                # Additional API endpoints
```

### File Format Support

**Model Formats:**
- `.gguf` - Native SD.cpp format (Q2_K, Q4_K, Q8_0 quantization)
- `.safetensors` - Standard format (recommended)
- `.ckpt`, `.pth` - PyTorch checkpoint formats

**Image Formats:**
**Input (SD.cpp CLI):**
- `.png`, `.jpg`/`.jpeg`, `.bmp`

**Output (SD.cpp CLI):**
- `.png` by default
- `.jpg`/`.jpeg`/`.jpe` when the output path uses a JPEG extension

**SwarmUI backend:**
- SD.cpp output is requested as PNG; SwarmUI can convert images to other formats if needed.

## Performance Benchmarks
----------------------

- **TODO**: Add benchmark results for CPU vs CUDA vs Vulkan.
- **TODO**: Include "first run" vs "repeat run" measurements and note when (if ever) caching improves throughput.
- **TODO**: Provide test settings (model, resolution, steps, sampler, cache settings) so results are reproducible.

## Contributing
------------

Contributions welcome! Focus areas:
- Performance profiling and optimization Image gen is very slow currently.
- Better error messages and user guidance
- UI/UX improvements
- Swarm parameter fixes

## License
-------

MIT License

## Credits
-------

- [stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp) by leejet
- [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) by mcmonkey

---

**Last Updated:** January 2026
**Extension Version:** 0.1.0
**Minimum SD.cpp Version:** master-1896b28
