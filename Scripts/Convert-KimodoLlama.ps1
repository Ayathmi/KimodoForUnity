# SPDX-License-Identifier: Apache-2.0
<#
.SYNOPSIS
Converts Ollama FP16 GGUF tensors into sharded local Transformers weights without loading an inference model.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PythonExecutable,
    [Parameter(Mandatory=$true)][string]$OllamaModelsDirectory,
    [Parameter(Mandatory=$true)][string]$EncoderDirectory,
    [Parameter(Mandatory=$true)][string]$BaseDirectory
)
$ErrorActionPreference = 'Stop'
$code = @'
import json, os, re, shutil, sys, uuid
from pathlib import Path
import numpy as np
import torch
from gguf import GGUFReader, GGMLQuantizationType
from safetensors.torch import save_file
from transformers import LlamaConfig, LlamaForCausalLM
from transformers.modeling_gguf_pytorch_utils import LlamaTensorProcessor, get_gguf_hf_weights_map, load_gguf_checkpoint

TAG = 'llama3:8b-instruct-fp16'

def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')

def convert_tensors(blob, config, output, shard_limit=2_000_000_000):
    # The meta model supplies names and shapes only; no model weights are allocated on CPU or GPU.
    with torch.device('meta'):
        skeleton = LlamaForCausalLM(config)
    expected = {name: tuple(value.shape) for name, value in skeleton.state_dict().items()}
    processor = LlamaTensorProcessor(config.to_dict())
    mapping = get_gguf_hf_weights_map(skeleton, processor)
    del skeleton
    reader = GGUFReader(str(blob))
    state, weight_map, shard_bytes, total_bytes, shard_number = {}, {}, 0, 0, 0

    def flush():
        nonlocal state, shard_bytes, shard_number
        if not state:
            return
        shard_number += 1
        name = f'model-{shard_number:05d}.safetensors'
        save_file(state, str(output / name), metadata={'format': 'pt'})
        for key in state:
            weight_map[key] = name
        state, shard_bytes = {}, 0
        print('Saved ' + name, flush=True)

    for tensor in reader.tensors:
        if tensor.tensor_type not in (GGMLQuantizationType.F16, GGMLQuantizationType.F32):
            raise ValueError('Only the FP16 Ollama variant is supported; quantized GGUF weights are not accepted.')
        name = mapping.get(tensor.name)
        if name is None or name in weight_map or name in state:
            raise ValueError('Unexpected or duplicate GGUF tensor: ' + tensor.name)
        # Use Transformers' Llama conversion, including the inverse Q/K rotary permutation.
        converted = processor.process(tensor.data, tensor.name).weights
        if tuple(converted.shape) != expected.get(name):
            raise ValueError('GGUF tensor shape does not match the adapter architecture: ' + name)
        size = converted.nbytes
        if state and shard_bytes + size > shard_limit:
            flush()
        state[name] = torch.from_numpy(np.array(converted, copy=True, order='C'))
        shard_bytes += size
        total_bytes += size
    flush()
    if set(weight_map) != set(expected):
        raise ValueError('GGUF model is missing required tensors: ' + ', '.join(sorted(set(expected) - set(weight_map))))
    write_json(output / 'model.safetensors.index.json', {'metadata': {'total_size': total_bytes}, 'weight_map': weight_map})
    return len(weight_map), total_bytes

def main():
    storage, encoder, destination = [Path(value).resolve() for value in sys.argv[1:4]]
    if storage == destination or storage in destination.parents or destination in storage.parents:
        raise ValueError('Ollama storage and converted weights must use separate directories.')
    if encoder == destination or encoder in destination.parents or destination in encoder.parents:
        raise ValueError('Converted weights must be separate from the downloaded adapter.')
    manifest_path = storage / 'manifests/registry.ollama.ai/library/llama3/8b-instruct-fp16'
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    layers = [layer for layer in manifest['layers'] if layer['mediaType'] == 'application/vnd.ollama.image.model']
    if len(layers) != 1:
        raise ValueError('Expected one Ollama model layer.')
    layer = layers[0]

    def blob_path(digest):
        if not re.fullmatch(r'sha256:[0-9a-f]{64}', digest):
            raise ValueError('Invalid Ollama blob identifier.')
        return storage / 'blobs' / digest.replace(':', '-')

    blob = blob_path(layer['digest'])
    if blob.stat().st_size != layer['size']:
        raise ValueError('Ollama model download is incomplete.')
    marker = destination / 'kimodo-ollama.json'
    if marker.is_file():
        prepared = json.loads(marker.read_text(encoding='utf-8'))
        sizes = prepared.get('files', {})
        if prepared.get('schemaVersion') == 1 and prepared.get('digest') == layer['digest'] and sizes and all(
            Path(name).name == name and (destination / name).is_file() and (destination / name).stat().st_size == size
            for name, size in sizes.items()
        ):
            print('Converted Llama 3 weights are already prepared.', flush=True)
            return
    if destination.exists() and any(destination.iterdir()):
        raise ValueError('Select an empty converted-model directory; existing files will not be overwritten.')
    config_data = json.loads((encoder / 'config.json').read_text(encoding='utf-8'))
    gguf_config = load_gguf_checkpoint(str(blob), return_tensors=False)['config']
    required = {'hidden_size': 4096, 'intermediate_size': 14336, 'num_hidden_layers': 32,
                'num_attention_heads': 32, 'num_key_value_heads': 8, 'vocab_size': 128256}
    if gguf_config.get('model_type') != 'llama' or config_data.get('model_type') != 'llama':
        raise ValueError('Expected the Llama architecture.')
    for key, expected in required.items():
        if gguf_config.get(key) != expected or config_data.get(key) != expected:
            raise ValueError('Llama 3 8B architecture mismatch: ' + key)
    for key in ('max_position_embeddings', 'rope_theta', 'rms_norm_eps'):
        if not np.isclose(gguf_config.get(key, 0), config_data.get(key, 1), rtol=1e-6, atol=0):
            raise ValueError('Llama positional/normalization configuration mismatch: ' + key)
    config_data.pop('auto_map', None)
    config_data['architectures'] = ['LlamaForCausalLM']
    config_data['torch_dtype'] = 'float16'
    config = LlamaConfig.from_dict(config_data)
    config._attn_implementation = 'eager'
    staging = destination.with_name(destination.name + '.building-' + uuid.uuid4().hex)
    staging.mkdir(parents=True)
    print('Converting FP16 GGUF tensors: ' + str(blob), flush=True)
    count, total = convert_tensors(blob, config, staging)
    config.save_pretrained(str(staging))
    for name in ('tokenizer.json', 'tokenizer_config.json', 'special_tokens_map.json'):
        shutil.copyfile(encoder / name, staging / name)
    licenses = [blob_path(item['digest']).read_text(encoding='utf-8') for item in manifest['layers']
                if item['mediaType'] == 'application/vnd.ollama.image.license']
    if not licenses:
        raise ValueError('The Ollama manifest contains no model license.')
    (staging / 'LICENSE').write_text('\n\n'.join(licenses), encoding='utf-8')
    write_json(staging / 'kimodo-ollama.json', {
        'schemaVersion': 1, 'tag': TAG, 'digest': layer['digest'], 'tensorCount': count,
        'totalBytes': total, 'source': 'https://ollama.com/library/llama3:8b-instruct-fp16',
        'files': {item.name: item.stat().st_size for item in staging.iterdir() if item.is_file()}
    })
    if destination.exists():
        destination.rmdir()
    staging.rename(destination)
    print('Prepared ' + str(count) + ' tensors in ' + str(destination), flush=True)

if __name__ == '__main__':
    main()
'@
& $PythonExecutable -u -c $code $OllamaModelsDirectory $EncoderDirectory $BaseDirectory
exit $LASTEXITCODE
