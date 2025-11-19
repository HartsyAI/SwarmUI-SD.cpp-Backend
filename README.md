# SwarmUI StableDiffusion.cpp Backend

A high-performance backend for SwarmUI using [StableDiffusion.cpp](https://github.com/leejet/stable-diffusion.cpp) - enabling fast, efficient image generation with Flux, SDXL, and SD 1.5 models in pure C++.

## Features

- ✅ **Full Flux Support** - Flux.1-dev and Flux.1-schnell with automatic GGUF conversion & component discovery
- ✅ **Multi-Architecture** - Flux, SD3/3.5, SDXL, SDXL-Turbo, SD 1.5, SD 2.x, LCM models
- ✅ **SwarmUI Integration** - Follows SwarmUI's backend patterns, model registration, and event system
- ✅ **Automatic Setup** - Downloads SD.cpp binaries automatically based on your hardware
- ✅ **GGUF Conversion** - Auto-converts Flux models to optimized GGUF format with quantization
- ✅ **Component Discovery** - Automatically finds multi-component model parts (Flux, SD3)
- ✅ **LoRA Support** - Full LoRA support across all architectures
- ✅ **Turbo Models** - Optimized parameters for SDXL-Turbo and SD1.5-Turbo variants
- ✅ **LCM Support** - Latent Consistency Models for ultra-fast 2-8 step generation
- ✅ **Memory Optimization** - VAE tiling, CPU offloading, and quantization options
- ✅ **Multiple Backends** - CPU, CUDA (NVIDIA), Vulkan, Metal (macOS)

## Quick Start

1. **Install the Extension**
   - Place this directory in `SwarmUI/src/Extensions/`
   - SwarmUI will automatically load it on startup

2. **First Run**
   - The extension will prompt to download SD.cpp for your platform
   - Choose CPU, CUDA, or Vulkan based on your hardware

3. **Add Models**
   - Place Flux models in `SwarmUI/Models/Stable-Diffusion/`
   - The backend will auto-convert to GGUF format on first use

## Flux Model Setup

Flux models require four components to work:

### Required Files

1. **Main Diffusion Model** (auto-converts to GGUF)
   - `flux1-dev.safetensors` OR `flux1-schnell.safetensors`
   - Download from: [FLUX.1-dev](https://huggingface.co/black-forest-labs/FLUX.1-dev)

2. **VAE** - `ae.safetensors`
   - Download: [ae.safetensors](https://huggingface.co/black-forest-labs/FLUX.1-dev/blob/main/ae.safetensors)

3. **CLIP-L Encoder** - `clip_l.safetensors`
   - Download: [clip_l.safetensors](https://huggingface.co/comfyanonymous/flux_text_encoders/blob/main/clip_l.safetensors)

4. **T5-XXL Encoder** - `t5xxl_fp16.safetensors`
   - Download: [t5xxl_fp16.safetensors](https://huggingface.co/comfyanonymous/flux_text_encoders/blob/main/t5xxl_fp16.safetensors)

### File Organization

Place files in one of these locations:

```
SwarmUI/Models/Stable-Diffusion/
├── flux1-dev.safetensors          # Main model
├── ae.safetensors                  # VAE
├── clip_l.safetensors              # CLIP-L
└── t5xxl_fp16.safetensors          # T5-XXL
```

OR organize by type:

```
SwarmUI/Models/
├── Stable-Diffusion/
│   └── flux1-dev.safetensors
├── VAE/
│   └── ae.safetensors
└── CLIP/
    ├── clip_l.safetensors
    └── t5xxl_fp16.safetensors
```

The backend will **automatically discover** components in either structure.

## Other Model Types

### SDXL / SDXL Turbo
- Standard safetensors or ckpt format
- Native 1024x1024 resolution
- Turbo variants optimized for 4-8 steps with CFG ~1.5-2.0

### SD 1.5 / SD 2.x
- Standard safetensors or ckpt format
- SD 1.5: 512x512 native
- SD 2.x: 768x768 native
- Full LoRA and VAE support

### LCM (Latent Consistency Models)
- Detected automatically by filename containing "lcm"
- Requires CFG = 1.0 and 2-8 steps
- Much faster than standard models
- Can be used as LoRA: `<lora:lcm-lora-sdv1-5:1>`

### SD3 / SD3.5 (Experimental)
- Multi-component architecture similar to Flux
- Requires: diffusion model + CLIP-G + CLIP-L + T5-XXL
- CFG scale ~4.5 recommended
- Component discovery similar to Flux

## GGUF Conversion

Flux models **must** be in GGUF format for SD.cpp. The backend handles this automatically.

### Automatic Conversion (Default)

On first use, the backend will:
1. Detect non-GGUF Flux models
2. Convert to GGUF with your chosen quantization
3. Save converted model alongside original
4. Use converted version for all future generations

**Note:** Conversion takes 5-15 minutes but only happens once.

### Manual Conversion

```bash
sd.exe -M convert -m flux1-dev.safetensors -o flux1-dev-q8_0.gguf --type q8_0
```

### Quantization Levels

| Level | VRAM Required | Quality | File Size |
|-------|--------------|---------|-----------|
| q8_0  | ~12GB        | Best    | ~12GB     |
| q4_0  | ~6-8GB       | Good    | ~6.4GB    |
| q3_k  | ~4-6GB       | Fair    | ~4.9GB    |
| q2_k  | ~4GB         | Lower   | ~3.7GB    |

**Recommendation:** Use `q8_0` for best quality, `q4_0` for 8GB GPUs, `q2_k` for 4GB GPUs.

## Configuration Settings

### Device Selection

```
Device: cpu | cuda | vulkan
```

- **CPU** - Universal, slower, works on any system
- **CUDA** - NVIDIA GPUs (best for Flux)
- **Vulkan** - Any modern GPU (⚠️ Flux may not work reliably)

### Flux-Specific Settings

```
FluxQuantization: q8_0 | q4_0 | q3_k | q2_k
```
- Choose based on available VRAM
- Affects conversion and memory usage

```
AutoConvertFluxToGGUF: true | false
```
- Automatically convert Flux models on first use
- Disable if you prefer manual conversion

```
FluxDevSteps: 20 (default)
FluxSchnellSteps: 4 (default)
```
- Default sampling steps for Flux variants
- Flux-dev: 20+ steps recommended
- Flux-schnell: 4 steps optimized

### Memory Optimization

```
VAETiling: true (recommended)
```
- Reduces memory usage by ~3GB during generation
- Essential for limited VRAM systems

```
VAEOnCPU: false
CLIPOnCPU: false
```
- Offload processing to CPU to save VRAM
- Enable if you're running out of memory

### Component Override Paths

```
FluxVAEPath: ""
FluxCLIPLPath: ""
FluxT5XXLPath: ""
```
- Optional: Specify exact paths to Flux components
- Leave empty for automatic discovery

## Usage Tips

### Flux Best Practices

1. **CFG Scale** - Always use 1.0 (backend enforces this)
2. **Sampler** - euler sampler works best (auto-selected)
3. **Steps** - Minimum 20 for flux-dev, 4 for flux-schnell
4. **Negative Prompts** - Not effective with Flux (warned but allowed)

### LoRA Support

LoRA syntax: `your prompt <lora:model_name:strength>`

**Important:** Flux LoRAs require q8_0 quantization for stability.

Place LoRA files in: `SwarmUI/Models/Lora/`

### Memory Requirements

**Flux-dev with q8_0:**
- 12GB+ VRAM recommended
- 8GB VRAM: Enable VAEOnCPU + VAETiling
- 4GB VRAM: Use q2_k quantization

**Flux-schnell with q4_0:**
- 6-8GB VRAM sufficient
- Faster inference (4 steps)

## Troubleshooting

### "Missing required Flux components"

**Problem:** VAE, CLIP-L, or T5-XXL not found

**Solution:**
1. Download missing components (links above)
2. Place in model directory or SwarmUI/Models/VAE or /CLIP
3. Check logs for search paths
4. Use override settings if needed

### "GGUF conversion failed"

**Problem:** Automatic conversion encountered error

**Solution:**
1. Check disk space (needs ~12GB free)
2. Enable debug mode for detailed logs
3. Try manual conversion
4. Set `AutoConvertFluxToGGUF = false` and use pre-converted models

### Vulkan + Flux Issues

**Problem:** Generation fails with Vulkan backend

**Solution:**
- Flux may not work with Vulkan (SD.cpp limitation)
- Switch to CUDA (NVIDIA) or CPU backend
- Re-download appropriate binary in settings

### Out of Memory

**Problem:** CUDA out of memory or allocation failed

**Solution:**
1. Lower quantization (q8_0 → q4_0 → q2_k)
2. Enable VAETiling
3. Enable VAEOnCPU and CLIPOnCPU
4. Reduce image dimensions
5. Close other GPU applications

### Slow Generation

**Problem:** Image generation takes very long

**Solution:**
- Use GPU backend (CUDA/Vulkan) instead of CPU
- Lower quantization for faster inference
- Use Flux-schnell instead of Flux-dev
- Reduce steps (but not below minimums)

## Architecture Support

| Model Type | Status | Notes |
|------------|--------|-------|
| **Flux Family** | | |
| Flux.1-dev | ✅ Full | Requires GGUF, 4 components |
| Flux.1-schnell | ✅ Full | Faster, 4-step optimized |
| **SD3 Family** | | |
| SD3 / SD3.5 | ✅ Supported | Multi-component like Flux |
| **SDXL Family** | | |
| SDXL | ✅ Full | Standard safetensors/ckpt |
| SDXL Turbo | ✅ Full | Fast 4-8 step variant |
| **SD 1.x/2.x Family** | | |
| SD 1.5 | ✅ Full | Standard safetensors/ckpt |
| SD 1.5 Turbo | ✅ Full | Fast variant |
| SD 2.x | ✅ Full | 768x768 models |
| **LCM Models** | | |
| LCM / LCM-LoRA | ✅ Full | 2-8 step inference |

## Performance Benchmarks

**Flux-dev (1024x1024, 20 steps):**
- RTX 4090 (q8_0): ~30-40s
- RTX 3080 (q4_0): ~50-70s
- CPU (q2_k): 5-10 minutes

**Flux-schnell (1024x1024, 4 steps):**
- RTX 4090 (q8_0): ~8-12s
- RTX 3080 (q4_0): ~15-20s

## Technical Details

### Backend Architecture

- **CLI-based execution** - Each generation spawns SD.cpp process
- **Stateless** - No persistent memory between generations
- **Automatic cleanup** - Temp files removed after generation
- **Component caching** - Discovered paths reused across generations

### File Format Support

**Models:**
- `.safetensors` - Standard (auto-converts for Flux)
- `.gguf` - Native SD.cpp format
- `.ckpt`, `.bin` - Legacy formats
- `.sft` - Shortened safetensors

**Images:**
- PNG output (default)
- JPG/JPEG supported

## Development

### Extension Structure

```
SwarmUI-SD.cpp-Backend/
├── SDcppExtension.cs           # Main extension entry
├── Config/
│   └── SDcppSettings.cs        # User configuration
├── SwarmBackends/
│   └── SDcppBackend.cs         # Backend implementation
├── Utils/
│   ├── SDcppProcessManager.cs  # CLI execution
│   ├── SDcppDownloadManager.cs # Binary downloads
│   ├── FluxModelComponents.cs  # Component discovery
│   └── GGUFConverter.cs        # Model conversion
└── WebAPI/
    └── SDcppAPI.cs             # API endpoints
```

### Contributing

Contributions welcome! Areas for improvement:
- Additional model architecture support
- Performance optimizations
- Better error messages
- UI enhancements

## Credits

- [StableDiffusion.cpp](https://github.com/leejet/stable-diffusion.cpp) by leejet
- [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) by mcmonkey
- Flux models by [Black Forest Labs](https://huggingface.co/black-forest-labs)

## License

MIT License - See SwarmUI and StableDiffusion.cpp licenses for dependencies.
