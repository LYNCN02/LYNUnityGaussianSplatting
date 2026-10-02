# LYNOOK 共享文档入口

本工程 `worldstudio` 通过根目录 `shared-docs/` Git submodule 使用统一文档仓库 `LYNCN02/lynookdocs`。

- [完整导航](../shared-docs/README.md)
- [项目关系和职责](../shared-docs/architecture/project-map.md)
- [本体控制 API](../shared-docs/contracts/body-control-api.md)
- [设备账号 API](../shared-docs/contracts/device-account-api.md) / [架构](../shared-docs/architecture/device-account-architecture.md)
- [App 内容接口](../shared-docs/contracts/app-content-api.md)
- [世界包](../shared-docs/integration/world-package.md) / [World Config](../shared-docs/contracts/world-config.md) / [JSON 示例](../shared-docs/contracts/examples/README.md)
- [场景队列与录制](../shared-docs/integration/scene-queue-integration.md)

初始化当前锁定版本：`git submodule update --init --recursive`。文档更新流程见共享仓库 README；本工程自己的 `docs/` 继续记录本地搭建、实现、UI 与验证结果。
