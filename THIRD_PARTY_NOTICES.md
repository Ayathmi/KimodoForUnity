# Dependency notices

Kimodo for Unity is independently written C# Editor integration under Apache-2.0. NVIDIA does not author or endorse this plugin.

The plugin does not bundle Python, Kimodo source, model weights, tokenizer files, proprietary game assets, or third-party avatars.

- NVIDIA Kimodo code: Apache-2.0, with its own third-party attributions. https://github.com/nv-tlabs/kimodo
- Kimodo-SOMA-RP-v1.2 weights: NVIDIA Open Model License. https://huggingface.co/nvidia/Kimodo-SOMA-RP-v1.2
- LLM2Vec code: MIT, with separate upstream model terms. https://github.com/McGill-NLP/llm2vec
- Meta Llama 3: access and use are subject to the Meta Llama 3 Community License and acceptable-use terms. https://huggingface.co/meta-llama/Meta-Llama-3-8B-Instruct
- Ollama client: user-installed separately; MIT. https://github.com/ollama/ollama
- Llama 3 FP16 weights distributed through Ollama retain the Meta Llama 3 Community License. https://ollama.com/library/llama3:8b-instruct-fp16
- gguf Python package: MIT, installed externally for GGUF reading. https://github.com/ggml-org/llama.cpp/tree/master/gguf-py
- Hugging Face Transformers: Apache-2.0, installed externally and used for Llama tensor conversion. https://github.com/huggingface/transformers
- bitsandbytes: MIT, installed externally for CUDA INT8/NF4 inference. https://github.com/bitsandbytes-foundation/bitsandbytes
- Voxta merged INT8 and NF4 LLM2Vec encoders retain Meta Llama 3 terms and McGill-NLP adapter attribution. https://huggingface.co/voxta/Llama-3-8B-LLM2Vec-ARDY-INT8 and https://huggingface.co/voxta/Llama-3-8B-LLM2Vec-ARDY-NF4

Built with Meta Llama 3.
