# worldstudio 工程协作

<!-- BEGIN LYNOOK SHARED DOCS -->
## LYNOOK 跨项目文档

- 跨项目需求、架构、API/消息协议、世界数据格式和联调流程统一在根目录 `shared-docs/`（`LYNCN02/lynookdocs` Git submodule）维护。先读 `shared-docs/README.md` 与 `shared-docs/architecture/project-map.md`，再读任务对应契约。
- 旧工程 Markdown 路径是跳转入口；不要在本工程恢复或新增共享协议副本。工程本地搭建、UI、实现细节及运行/部署验证记录仍放在本工程 `docs/`。
- JSON 文档示例统一位于 `shared-docs/contracts/examples/`。迁入文档保留历史日期和验证边界，实际支持范围以拥有实现的工程源码和当前联调记录确认。
- 子模块为空时运行 `git submodule update --init --recursive`；该命令恢复工程锁定的版本。共享协议升级需先提交并推送文档仓库，再选择已审阅的 commit 并提交本工程 `shared-docs` 引用。
- 推荐在独立 `lynookdocs` 工作区修改共享文档；直接编辑子模块前确认分支及工作树，避免丢弃已有修改或在 detached HEAD 中留下未推送提交。
<!-- END LYNOOK SHARED DOCS -->
