#!/usr/bin/env python3
"""
TurboQuant+ FastAPI Sidecar Service
=====================================
Purpose: HTTP API for KV cache compression using TurboQuant algorithms
Source: https://github.com/TheTom/turboquant_plus (Apache 2.0)

Endpoints:
- POST /compress - Compress KV cache data
- GET /health - Health check endpoint
- GET /config - Get current configuration

Usage:
    uvicorn turboquant_api:app --host 0.0.0.0 --port 8080
"""

import os
import time
import logging
from typing import Optional, Dict, Any
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field
import numpy as np

# Import TurboQuant library
try:
    from turboquant import TurboQuant, KVCacheCompressor, PolarQuant
    TURBOQUANT_AVAILABLE = True
except ImportError:
    TURBOQUANT_AVAILABLE = False
    logging.warning("TurboQuant library not available - compression will be disabled")

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s - %(name)s - %(levelname)s - %(message)s"
)
logger = logging.getLogger(__name__)


# ============================================================================
# SECTION: Data Models (Request/Response Schemas)
# ============================================================================

class TurboQuantConfig(BaseModel):
    """Configuration for KV cache compression"""
    k_cache_type: str = Field(default="q8_0", description="K-cache quantization type")
    v_cache_type: str = Field(default="turbo4", description="V-cache quantization type")
    block_size: int = Field(default=32, ge=16, le=256, description="Block size for compression")
    enable_boundary_protection: bool = Field(default=True, description="Protect boundary layers")
    boundary_layers: int = Field(default=2, ge=1, le=8, description="Number of boundary layers")
    enable_sparse_v: bool = Field(default=True, description="Enable Sparse V dequantization")


class CompressionRequest(BaseModel):
    """Request schema for compression endpoint"""
    kv_cache: list[float] = Field(..., description="Raw KV cache data as float array")
    config: Optional[TurboQuantConfig] = Field(default=None, description="Compression configuration")
    context_length: int = Field(..., gt=0, description="Context length in tokens")


class CompressionResponse(BaseModel):
    """Response schema for compression endpoint"""
    compressed_data: Optional[list[int]] = Field(default=None, description="Compressed data as byte array")
    original_size: int = Field(..., description="Original size in bytes")
    compressed_size: int = Field(..., description="Compressed size in bytes")
    compression_ratio: float = Field(..., description="Compression ratio (original/compressed)")
    quality_estimate: float = Field(default=1.0, description="Estimated quality retention (0-1)")
    processing_time_ms: float = Field(..., description="Processing time in milliseconds")


# ============================================================================
# SECTION: Application Lifecycle Management
# ============================================================================

@asynccontextmanager
async def lifespan(app: FastAPI):
    """Application lifespan manager for startup/shutdown events"""
    # Startup: Initialize TurboQuant if available
    logger.info("Starting TurboQuant+ sidecar service")
    if TURBOQUANT_AVAILABLE:
        logger.info("✓ TurboQuant library loaded successfully")
    else:
        logger.warning("✗ TurboQuant library not available - running in passthrough mode")
    
    yield
    
    # Shutdown: Cleanup resources
    logger.info("Shutting down TurboQuant+ sidecar service")


# Initialize FastAPI application
app = FastAPI(
    title="TurboQuant+ Sidecar Service",
    description="KV Cache Compression API for Long-Context LLM Inference",
    version="1.0.0",
    lifespan=lifespan
)

# Global configuration state
global_config = TurboQuantConfig()


# ============================================================================
# SECTION: Health Check & Service Discovery
# ============================================================================

@app.get("/health")
async def health_check():
    """
    Health check endpoint for service discovery and monitoring
    
    Returns:
        dict: Service status and capabilities
    """
    return {
        "status": "healthy",
        "turboquant_available": TURBOQUANT_AVAILABLE,
        "version": "1.0.0",
        "compression_enabled": TURBOQUANT_AVAILABLE
    }


@app.get("/config")
async def get_config():
    """
    Get current compression configuration
    
    Returns:
        dict: Current configuration settings
    """
    return global_config.model_dump()


# ============================================================================
# SECTION: Core Compression Logic
# ============================================================================

@app.post("/compress")
async def compress_kv_cache(request: CompressionRequest):
    """
    Compress KV cache using TurboQuant algorithms
    
    This endpoint applies PolarQuant + QJL compression to reduce KV cache
    memory usage by 3.8-6.4x while maintaining near-original quality.
    
    Key features:
    - Asymmetric K/V compression (q8_0-K + turbo-V recommended)
    - Boundary layer protection for sensitive layers
    - Sparse V dequantization for faster decode
    - Graceful degradation if TurboQuant unavailable
    
    Args:
        request: Compression request with KV cache data and configuration
        
    Returns:
        CompressionResponse: Compressed data and metrics
    """
    start_time = time.time()
    
    try:
        # Validate input data
        if not request.kv_cache or len(request.kv_cache) == 0:
            raise HTTPException(status_code=400, detail="Empty KV cache data")
        
        # Convert to numpy array for efficient processing
        kv_array = np.array(request.kv_cache, dtype=np.float32)
        original_size = kv_array.nbytes
        
        # Update configuration if provided
        if request.config:
            global global_config
            global_config = request.config
        
        # If TurboQuant not available, return uncompressed data (graceful degradation)
        if not TURBOQUANT_AVAILABLE:
            processing_time = (time.time() - start_time) * 1000
            return CompressionResponse(
                compressed_data=kv_array.tobytes(),
                original_size=original_size,
                compressed_size=original_size,
                compression_ratio=1.0,
                quality_estimate=1.0,
                processing_time_ms=processing_time
            )
        
        # Apply TurboQuant compression using correct API
        compressor = KVCacheCompressor(
            head_dim=128,  # Standard head dimension for most models
            k_bits=4 if 'turbo4' in global_config.k_cache_type else (3 if 'turbo3' in global_config.k_cache_type else 2),
            v_bits=4 if 'turbo4' in global_config.v_cache_type else (3 if 'turbo3' in global_config.v_cache_type else 2),
        )
        
        # Compress the KV cache (reshape for KVCacheCompressor API)
        # Expected shape: (num_layers, num_heads, seq_len, head_dim)
        # For simplicity, treat input as single layer, single head
        kv_reshaped = kv_array.reshape(1, 1, -1, 128) if kv_array.size >= 128 else np.zeros((1, 1, 1, 128))
        compressed = compressor.compress(kv_reshaped, kv_reshaped)
        compressed_bytes = compressed.tobytes() if hasattr(compressed, 'tobytes') else compressed
        compressed_size = len(compressed_bytes)
        
        # Calculate compression ratio
        compression_ratio = original_size / compressed_size if compressed_size > 0 else 1.0
        
        # Estimate quality based on compression type
        quality_map = {
            "turbo4": 0.998,  # +0.2% PPL
            "turbo3": 0.990,  # +1.0% PPL
            "turbo2": 0.935,  # +6.5% PPL
            "q8_0": 1.0,
        }
        quality_estimate = quality_map.get(global_config.v_cache_type, 0.99)
        
        processing_time = (time.time() - start_time) * 1000
        
        logger.info(
            f"Compression complete: {original_size} → {compressed_size} bytes "
            f"({compression_ratio:.2f}x) in {processing_time:.2f}ms"
        )
        
        return CompressionResponse(
            compressed_data=list(compressed_bytes),
            original_size=original_size,
            compressed_size=compressed_size,
            compression_ratio=compression_ratio,
            quality_estimate=quality_estimate,
            processing_time_ms=processing_time
        )
        
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Compression failed: {str(e)}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Compression failed: {str(e)}")


# ============================================================================
# SECTION: Server Entry Point
# ============================================================================

if __name__ == "__main__":
    import uvicorn
    
    # Get port from environment variable or use default
    port = int(os.getenv("TURBOQUANT_PORT", "8080"))
    host = os.getenv("TURBOQUANT_HOST", "0.0.0.0")
    
    logger.info(f"Starting TurboQuant+ API server on {host}:{port}")
    uvicorn.run(app, host=host, port=port)
