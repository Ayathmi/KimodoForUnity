// SPDX-License-Identifier: Apache-2.0
namespace KimodoForUnity
{
    /// <summary>Process-local adapters for the installed official Python API; never patches the environment.</summary>
    public static class KimodoRuntimeBridge
    {
        /// <summary>Loads either the original adapter chain or a pre-merged quantized encoder on CUDA.</summary>
        public const string EncoderCode = @"
import os
import json
import numpy as np
import torch
from kimodo.scripts import run_text_encoder_server as server
from kimodo.model.llm2vec.llm2vec import LLM2Vec

class FixedDeviceEncoder(LLM2Vec):
    def to(self, device=None, *args, **kwargs):
        if device is not None and str(device) not in ('cuda', 'cuda:0'):
            raise ValueError('Quantized encoders must remain on CUDA device 0.')
        return self

class MergedEncoder:
    def __init__(self, directory, preset):
        if os.environ.get('TEXT_ENCODER_DEVICE', 'cuda') != 'cuda' or not torch.cuda.is_available():
            raise RuntimeError('INT8 and NF4 model combinations require CUDA.')
        with open(os.path.join(directory, 'config.json'), encoding='utf-8') as stream:
            config = json.load(stream)
        quant = config.get('quantization_config', {})
        valid = (quant.get('load_in_8bit') and not quant.get('load_in_4bit')) if preset == 'Int8' else (
            quant.get('load_in_4bit') and not quant.get('load_in_8bit') and quant.get('bnb_4bit_quant_type') == 'nf4')
        if quant.get('quant_method') != 'bitsandbytes' or not valid:
            raise ValueError('Encoder format does not match the selected combination.')
        if os.path.exists(os.path.join(directory, 'adapter_config.json')):
            raise ValueError('The selected quantized encoder must already contain both merged adapters.')
        self.model = FixedDeviceEncoder.from_pretrained(
            directory, peft_model_name_or_path=None, enable_bidirectional=True,
            torch_dtype=torch.bfloat16, device_map={'': 'cuda:0'},
            local_files_only=True, trust_remote_code=False,
            pooling_mode='mean', max_length=512, doc_max_length=400, skip_instruction=True)
        self.model.model.config._name_or_path = 'meta-llama/Meta-Llama-3-8B-Instruct'
        self.model.eval()
        for parameter in self.model.parameters():
            parameter.requires_grad = False
        print('Loaded merged LLM2Vec ' + preset + ' on CUDA; adapters are not applied again.', flush=True)

    def __call__(self, text):
        single = isinstance(text, str)
        with torch.inference_mode():
            encoded = self.model.encode([text] if single else text, batch_size=1,
                show_progress_bar=False, device='cuda:0')
        encoded = np.asarray(encoded)
        if encoded.ndim != 2 or encoded.shape[1] != 4096 or not np.isfinite(encoded).all():
            raise ValueError('Expected finite 4096-dimensional LLM2Vec embeddings.')
        tensor = torch.as_tensor(encoded[:, None], device='cuda:0')
        return (tensor[0], 1) if single else (tensor, [1] * len(encoded))

def start_encoder():
    preset = os.environ.get('KIMODO_UNITY_PRESET', 'Fp16')
    if preset == 'Fp16':
        server.TEXT_ENCODER_PRESETS['llm2vec']['kwargs']['dtype'] = 'float16'
    elif preset in ('Int8', 'Nf4'):
        server._build_text_encoder = lambda name, fp32=False: MergedEncoder(os.environ['KIMODO_UNITY_ENCODER'], preset)
    else:
        raise ValueError('Unsupported model combination: ' + preset)
    server.main()

if __name__ == '__main__':
    start_encoder()
";

        /// <summary>Resamples timestamps and rotations without applying a motion smoothing filter.</summary>
        public const string ResampleCode = @"
import os
import numpy as np
import torch
from kimodo.geometry import matrix_to_quaternion, quaternion_to_matrix
from kimodo.skeleton import global_rots_to_local_rots
from kimodo.skeleton.registry import build_skeleton
from kimodo.exports.bvh import save_motion_bvh

def resample_motion(source, source_fps, target_fps, seconds, skeleton):
    if not 1 <= target_fps <= 60 or int(target_fps) != target_fps:
        raise ValueError('Frame rate must be an integer from 1 to 60.')
    if seconds < 1 or int(seconds) != seconds or not np.isfinite(seconds):
        raise ValueError('Duration must be a positive integer.')
    rotations = torch.as_tensor(source['global_rot_mats'], dtype=torch.float32)
    local = global_rots_to_local_rots(rotations, skeleton)
    root = np.asarray(source['posed_joints'][:, skeleton.root_idx], dtype=np.float32)
    count = len(root)
    if count < 1 or not np.isfinite(source_fps) or source_fps <= 0:
        raise ValueError('Invalid native motion timing.')
    if abs(count / source_fps - seconds) > 1.01 / source_fps:
        raise ValueError('Native motion duration differs from the request.')
    # Include an endpoint at exactly the requested duration; hold the last native pose past its sample time.
    time = np.arange(int(seconds * target_fps) + 1, dtype=np.float64) / target_fps
    sample = np.clip(time * source_fps, 0, count - 1)
    first = np.floor(sample).astype(np.int64)
    second = np.minimum(first + 1, count - 1)
    fraction = torch.as_tensor(sample - first, dtype=torch.float32).view(-1, 1, 1)
    quaternion = matrix_to_quaternion(local)
    a, b = quaternion[first], quaternion[second]
    dot = (a * b).sum(-1, keepdim=True)
    b = torch.where(dot < 0, -b, b)
    dot = dot.abs().clamp(0, 1)
    angle = torch.acos(dot)
    divisor = torch.sin(angle).clamp_min(1e-8)
    spherical = (torch.sin((1 - fraction) * angle) * a + torch.sin(fraction * angle) * b) / divisor
    linear = (1 - fraction) * a + fraction * b
    q = torch.where(dot > 0.9995, linear, spherical)
    q = torch.nn.functional.normalize(q, dim=-1)
    local_out = quaternion_to_matrix(q)
    weight = (sample - first).astype(np.float32)[:, None]
    root_out = torch.from_numpy((1 - weight) * root[first] + weight * root[second])
    global_out, posed, _ = skeleton.fk(local_out, root_out)
    nearest = np.minimum(np.floor(sample + 0.5).astype(np.int64), count - 1)
    output = {key: (value[nearest] if isinstance(value, np.ndarray) and value.ndim > 0 and len(value) == count else value)
              for key, value in source.items()}
    if 'smooth_root_pos' in source:
        value = source['smooth_root_pos']
        output['smooth_root_pos'] = (1 - weight) * value[first] + weight * value[second]
    if 'global_root_heading' in source:
        value = source['global_root_heading']
        angles = np.unwrap(np.arctan2(value[:, 1], value[:, 0]))
        heading = np.interp(sample, np.arange(count), angles)
        output['global_root_heading'] = np.stack((np.cos(heading), np.sin(heading)), -1).astype(np.float32)
    output.update(local_rot_mats=local_out.numpy(), root_positions=root_out.numpy(),
        global_rot_mats=global_out.numpy(), posed_joints=posed.numpy(),
        fps=np.asarray(target_fps, dtype=np.float32), source_fps=np.asarray(source_fps, dtype=np.float32),
        duration=np.asarray(seconds, dtype=np.float32))
    for value in output.values():
        if isinstance(value, np.ndarray) and np.issubdtype(value.dtype, np.number) and not np.isfinite(value).all():
            raise ValueError('Resampled motion contains non-finite values.')
    return output

def export_resampled(native_path, output_stem, source_fps, target_fps, seconds):
    with np.load(native_path, allow_pickle=False) as archive:
        source = {key: np.asarray(archive[key]) for key in archive.files}
    skeleton = build_skeleton(source['posed_joints'].shape[1]).to('cpu')
    output = resample_motion(source, source_fps, target_fps, seconds, skeleton)
    if os.path.exists(output_stem + '.npz') or os.path.exists(output_stem + '.bvh'):
        raise FileExistsError('Output files already exist.')
    np.savez(output_stem + '.npz', **output)
    save_motion_bvh(output_stem + '.bvh', torch.from_numpy(output['local_rot_mats']),
        torch.from_numpy(output['root_positions']), skeleton=skeleton,
        fps=target_fps, standard_tpose=True)
    print(f'Output: {target_fps} fps, {seconds} seconds, {len(output[""root_positions""])} samples including endpoint.', flush=True)
";

        /// <summary>Runs official generation at its native rate and exports consistent BVH and NPZ timing.</summary>
        public const string GenerationCode = ResampleCode + @"
import sys
from kimodo.scripts import generate

output_stem, target_fps, seconds = sys.argv[1:4]
sys.argv = [sys.argv[0]] + sys.argv[4:]
native_fps = []
original_load = generate.load_model
def capture_native_rate(*args, **kwargs):
    result = original_load(*args, **kwargs)
    model = result[0] if isinstance(result, tuple) else result
    native_fps.append(float(model.fps))
    return result
generate.load_model = capture_native_rate
generate.main()
export_resampled(output_stem + '-native.npz', output_stem, native_fps[0], int(target_fps), int(seconds))
";
    }
}
