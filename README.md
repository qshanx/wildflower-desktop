# 窗外 · Wildflower Desktop

油画风互动桌面。池塘花园是第一个主题：开木篮选种，在草地种花，按电脑本地时间生长，剪花插瓶，夜间萤火虫。

**当前为开发预览，Windows 真实桌面与升级验收尚未完成，不作为稳定版本分发。**

Windows 版采用轻量原生宿主与系统 WebView2；软件与主题资源独立更新，GitHub Releases 提供下载，个人花园只保存在本机。未来更多主题使用同一安装入口。

在 Mac 上运行 `bash scripts/build-mac.sh`，生成 `artifacts/macos/窗外.app`，打开即可试玩。脚本也接受输出路径和 Bundle ID，可构建独立测试副本。

- [产品需求与实现状态](docs/PRD.md)
- [架构与更新边界](ARCHITECTURE.md)
- [交付和实测记录](docs/EXECUTION.md)

参考体验来自用户指定的[互动花园视频](https://xhslink.cn/o/2Iz6cf26aZO)。本项目独立实现，不是原作者发行版。当前尚未接入真实天气。
