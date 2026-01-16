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

SwarmUI parameters are organized into standard Swarm groups (Sampling / Advanced Sampling / Advanced Video / Advanced Model Addons / Refine-Upscale / ControlNet), plus SD.cpp-specific groups (VRAM/Memory and Performance/Caching).

Below is the definitive list of SD.cpp-related parameters and what SD.cpp CLI arguments they emit.

#### SD.cpp VRAM / Memory (Advanced)

| Swarm Parameter | SD.cpp CLI Arg | Notes |
|---|---|---|
| VAE Tiling | `--vae-tiling` | Reduces VRAM usage by decoding VAE in tiles (slower). |
| VAE on CPU | `--vae-on-cpu` | Saves VRAM, much slower. |
| CLIP on CPU | `--clip-on-cpu` | Saves VRAM, slower prompt encoding. |
| Offload Model Weights to CPU | `--offload-to-cpu` | Keeps weights in RAM, loads into VRAM as needed. |
| ControlNet on CPU | `--control-net-cpu` | Only affects jobs that use ControlNet. |
| SD.cpp VAE Tile Size | `--vae-tile-size` | SD.cpp-specific format `XxY` (only relevant when VAE Tiling is enabled). |
| SD.cpp VAE Relative Tile Size | `--vae-relative-tile-size` | SD.cpp-specific format `XxY` (overrides `--vae-tile-size`). |
| SD.cpp VAE Tile Overlap | `--vae-tile-overlap` | Fractional overlap (default 0.5). |
| Force SDXL VAE Conv Scale | `--force-sdxl-vae-conv-scale` | Only relevant for SDXL VAEs. |

#### SD.cpp Performance / Caching (Advanced)

| Swarm Parameter | SD.cpp CLI Arg | Notes |
|---|---|---|
| Memory Map Models | `--mmap` | Usually speeds up model load and reduces RAM usage. |
| VAE Direct Convolution | `--vae-conv-direct` | Usually faster VAE decoding. |
| Cache Mode | `--cache-mode` | `auto` selects a mode based on model architecture. |
| Cache Preset | `--cache-preset` | Applies to `cache-dit`. |
| Cache Option | `--cache-option` | Advanced free-form cache tuning string. |
| SCM Mask | `--scm-mask` | Cache-dit step mask (comma-separated 0/1). |
| SCM Policy | `--scm-policy` | `dynamic` (default) or `static`. |

#### Sampling (Swarm group: Sampling)

| Swarm Parameter | SD.cpp CLI Arg | Notes |
|---|---|---|
| SD.cpp Sampler | `--sampling-method` | Euler recommended for Flux; euler_a typical for SD/SDXL. |
| SD.cpp Scheduler | `--scheduler` | Leave empty to use SD.cpp default. |

#### Advanced Sampling (Swarm group: Advanced Sampling)

| Swarm Parameter | SD.cpp CLI Arg | Notes |
|---|---|---|
| Flash Attention | `--diffusion-fa` | Performance/VRAM tradeoff depends on build/device. |
| Diffusion Direct Convolution | `--diffusion-conv-direct` | Performance optimization; disable if unstable on your device. |
| RNG | `--rng` | Random backend selection. |
| Sampler RNG | `--sampler-rng` | If unset, follows `--rng`. |
| Prediction Override | `--prediction` | Only change if model requires it. |
| Eta | `--eta` | DDIM/TCD only. |
| Custom Sigmas | `--sigmas` | Advanced override of sigma schedule. |
| SLG Scale | `--slg-scale` | DiT models only; 0 disables. |
| SLG Start | `--skip-layer-start` | Requires SLG enabled. |
| SLG End | `--skip-layer-end` | Requires SLG enabled. |
| SLG Skip Layers | `--skip-layers` | Requires SLG enabled. |
| Timestep Shift | `--timestep-shift` | NitroFusion models only. |
| Preview Method Override | `--preview` | Per-job override; backend setting still controls preview enable. |
| Preview Interval | `--preview-interval` | Per-job override. |
| Preview Noisy | `--preview-noisy` | Per-job override. |
| TAESD Preview Only | `--taesd-preview-only` | Per-job override. |

#### Refine / Upscale (Swarm group: Refine / Upscale)

| Swarm Parameter | SD.cpp CLI Arg | Notes |
|---|---|---|
| ESRGAN Upscale Model | `--upscale-model` | Enables ESRGAN post-upscaling. |
| Upscale Repeats | `--upscale-repeats` | Number of ESRGAN passes. |

#### ControlNet (Swarm group: ControlNet)

| Swarm Parameter | SD.cpp CLI Arg | Notes |
|---|---|---|
| ControlNet Model | `--control-net` | Only first ControlNet is used. |
| ControlNet Image | `--control-image` | If missing, init image may be used as fallback. |
| Control Strength | `--control-strength` | Strength for ControlNet conditioning. |
| ControlNet Canny Preprocessor | `--canny` | Applies SD.cpp canny preprocessor to the control image. |

#### Advanced Video (Swarm group: Advanced Video)

| Swarm Parameter | SD.cpp CLI Arg | Notes |
|---|---|---|
| Video FPS | `--fps` | Used for video generation. |
| Video End Frame | `--end-img` | Required by some image-to-video workflows. |
| Control Video Frames Directory | `--control-video` | Directory containing ordered frame images. |
| Flow Shift | `--flow-shift` | Default 3.0 for Wan; can be overridden. |
| MoE Boundary | `--moe-boundary` | Wan2.2 specific. |
| VACE Strength | `--vace-strength` | Wan specific. |

#### Advanced Model Addons (Swarm group: Advanced Model Addons)

| Swarm Parameter | SD.cpp CLI Arg | Notes |
|---|---|---|
| TAESD Preview Decoder | `--taesd` | Select a TAESD model used for preview decoding. |
| CLIP Vision Model | `--clip_vision` | Only needed for architectures that require it. |
| LLM Vision Model | `--llm_vision` | Only needed for architectures that require it. |
| Embeddings Directory | `--embd-dir` | Optional embeddings folder. |
| Weight Type | `--type` | Overrides SD.cpp weight type selection. |
| Tensor Type Rules | `--tensor-type-rules` | Advanced per-tensor type control. |
| LoRA Apply Mode | `--lora-apply-mode` | Controls how SD.cpp applies LoRAs. |
| PhotoMaker Model | `--photo-maker` | Enables PhotoMaker support when set. |
| PhotoMaker ID Images Directory | `--pm-id-images-dir` | PhotoMaker input ID images folder. |
| PhotoMaker ID Embed Path | `--pm-id-embed-path` | PhotoMaker v2 embed path. |
| PhotoMaker Style Strength | `--pm-style-strength` | PhotoMaker strength. |

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
