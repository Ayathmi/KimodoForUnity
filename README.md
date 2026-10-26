# Kimodo for Unity

插件代码是AI写的，别看了，能跑就行（？

Unity Editor integration for NVIDIA Kimodo

集成NVIDIA Kimodo动画生成模型到Unity

Licensed under Apache-2.0.

Created by GitHub: [@Ayathmi](https://github.com/Ayathmi).

## 安装

克隆本仓库，将其置于Unity Assets文件夹中的任意位置，然后运行“Tools”->“Kimodo for Unity”->“Setup Wizard”，并跟随安装向导进行操作

或者从Realease页面下载最新的unitypackage，导入到项目中

在开始安装向导之前，需要做如下的预先准备：

1. 安装Python、Ollama、Ninja、GCC、G++。
2. 确保`RAM >= 16GB`，`VRAM >= 8GB`。

怎么挑选你的版本：

1. 显存8G选NF4，典型的例子有4060（含笔记本）、4070（含笔记本）、5060（含笔记本）、5070（含笔记本）、2070、2080、3060 8G（仅桌面端）、1080
2. 显存12G起，可以选INT8，典型例子有3060 12G（仅桌面端和笔记本魔改）、4070Ti（含笔记本）、5070Ti（含笔记本）、4080（笔记本）
3. 显存16G起，可以选择FP16，典型例子有4060Ti 16G（仅桌面端）、5060Ti 16G（仅桌面端）、4070TiS、4080、5070Ti、5080、4090（笔记本）、5080（笔记本）

> 注意：作者在实测运行FP16版本时观测到RAM峰值占用为52.9GB、显存占用为15.6GB；对应的，INT8版本RAM峰值为约33GB，显存占用11.3GB；NF4版本RAM峰值则为24GB，显存峰值为7.9GB

> 注意：笔记本用户请做好散热，本地运行AI是高负载工作，请给设备做好安全准备

> 注意：由于Windows内存管理策略在不同RAM容量的设备上表现不一致，RAM占用仅供参考（人话就是NF4版在32GB RAM的电脑上几乎一定能跑，INT8说不准，16G RAM可以试试但不建议

> 注意：本项目测试时使用Python 3.14、pybind 3.11 for Python 3.x、CUDA 12.8、Pytorch 2.10.0

> 注意：本项目基于Unity 6000.3.24f1构建

## 使用

运行“Tools”->“Kimodo for Unity”->“Motion Studio”

默认情况下，Motion Studio将使用CUDA作为Text Encoder和Kimodo模型的运行设备。FP16版本可在设置向导的“GPU”页选择以CPU运行；INT8／NF4版本只能用CUDA运行

请注意观察日志，Kimodo将会输出NPZ/BVH文件，具体路径将在日志中打印

可以在“预览与导入”面板导入BVH、指定一个绑好骨骼和Avatar的模型，查看效果。在高刷新率的显示器上，预览产生撕裂和抖动是预期现象，导出到Unity使用时是没有抖动的

## 参考

- [Official Kimodo installation](https://research.nvidia.com/labs/sil/projects/kimodo/docs/getting_started/installation.html)
- [PyTorch CUDA wheels](https://pytorch.org/get-started/locally/)
- [pybind11 Python 3.14 support](https://pybind11.readthedocs.io/en/stable/changelog.html)
- [NVIDIA system-memory fallback](https://nvidia.custhelp.com/app/answers/detail/a_id/5490)
- [Hugging Face CLI](https://huggingface.co/docs/huggingface_hub/en/guides/cli)
- [Install Ollama](https://ollama.com/download)
- [Llama 3 8B](https://ollama.com/library/llama3:8b)
- [Llama 3 8B Instruct FP16](https://ollama.com/library/llama3:8b-instruct-fp16)
- [LLM2Vec INT8](https://huggingface.co/voxta/Llama-3-8B-LLM2Vec-ARDY-INT8)
- [LLM2Vec NF4](https://huggingface.co/voxta/Llama-3-8B-LLM2Vec-ARDY-NF4)
- [bitsandbytes](https://huggingface.co/docs/bitsandbytes/en/installation)

Built with Meta Llama 3. Dependency licenses remain separate; see THIRD_PARTY_NOTICES.md. This plugin is not an NVIDIA product.
