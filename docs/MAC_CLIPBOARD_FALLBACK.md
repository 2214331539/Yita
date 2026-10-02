# Mac Cmd+C 回退

本实现位于 `codex/platform-host-services`，通过 Swift helper 执行，尚未接入 Desktop 的 Mac 自动拖选/快捷键。真实 Mac 授权、目标应用和 pasteboard 行为仍待验收。Windows 的 Ctrl+C 策略保持独立。

## 触发与权限

`MacSelectionAdapter.ReadAsync(request)` 只读取 AX。宿主必须显式调用 `ReadAsync(request, allowClipboardFallback: true)` 才能允许 helper 复制；JSON 请求对应 `allowClipboardFallback:true`。AX 已读取到文本时不再复制。只有空选区或不支持的 AX 结果可以进入回退；权限拒绝、保护内容、来源改变、取消、异常和 AX 超时停止此次读取。

回退需要 Accessibility 和事件投递权限，使用 `CGPreflightPostEventAccess` 检查后者，不自动弹出授权框。`permissions.eventPosting` 与 helper 的 `capabilities.clipboardFallback` 分别表示系统授权和代码能力；它们不代表 Desktop 的全局输入已完成。

复制前匹配本次前台 PID/Bundle ID，校验聚焦元素，检查聚焦/命中路径的安全控件与有界祖先。只接受可识别的文本角色，以及 Preview/Adobe/Skim 的部分文档角色；未知或非文本焦点保留手动复制入口。终端、密码控件、自身进程和按住 Command/Control/Shift/Option 的操作不发送复制。

## 剪贴板事务

1. 在不清空剪贴板的前提下保存所有 item/type 的已物化字节，最多 16 项、64 种格式、合计 16 MiB。空剪贴板可以保存；缺失数据、重复类型、文件 promise、隐藏或短暂内容标记等拒绝回退。备份期间序列改变则停止，不覆盖新内容。
2. 再次检查来源、焦点、输入计数和序列。使用带 Yita 标记的 private CGEventSource，向原 PID 投递 Command+C 按下/抬起；不向新前台发送快捷键。
3. 在最多 900ms 内观察 `NSPasteboard.changeCount`、文本和格式，并要求稳定至少 80ms。只接受从备份序列增加一次的候选；多次变化无法可靠归属，不翻译也不恢复。HID 键盘/鼠标按下计数变化时停止读取；来源或焦点改变则丢弃结果。
4. 返回前仅在当前序列仍为本事务已归属的序列时恢复原 item/type 字节。用户的后续复制优先；恢复失败则返回 `clipboardUnavailable`，不把复制结果当作正常翻译交付。

复制结果最多 20,000 个 UTF-16 单位，不静默截断，不附加旧剪贴板、其他上下文或推测的 AX 边界。备份只保存在 helper 内存中，不写入文件、日志或 IPC。

## 取消和退出

helper 协议升级到 2。`cancelSelection` 携带原请求 ID，是没有单独响应的控制帧。stdin 读取不被正在等待复制的主循环阻塞，控制帧可以标记正在运行的请求。

客户端的通常请求时限为 3 秒。允许复制的请求取消或超时后，先通知原 ID，最多再等待 2 秒，让 helper 观察已投递的复制、恢复剪贴板并返回原响应。原来的有界读取任务保持存活，因此收到半帧时不会重新读错位置。清理完成后，丢弃旧结果并传播取消；不能把旧结果交给下一次请求。

若响应不能完成，则关闭 stdin，给予 helper 最多 2 秒的退出清理时间，再结束卡死进程。新 helper 必须等待旧进程清理结束后才能启动。客户端释放、stdin EOF 或父进程消失会标记请求取消；helper 在当前事务收尾后退出。未允许复制的请求继续使用原来的直接隔离策略。

## 系统边界

macOS 没有 Windows 的剪贴板所有者校验，也没有 NSPasteboard 序列的原子 compare-and-swap。来源、输入计数、单次序列变化和稳定窗口只能减少误归属风险，不能证明更新一定来自注入的 Cmd+C。最终检查与恢复写入之间仍有极短竞态，外部进程的程序化复制可能不可区分；本实现不宣称绝对无竞态。

NSPasteboard 保存的是已物化格式，不能重建任意原应用的 lazy/provider 行为。helper 被强制结束、崩溃、pasteboard 服务卡死，或目标在观察时限后才执行复制时，可能无法恢复。常规取消/退出已经提供清理机制，但不能承诺对系统和目标进程的任意故障完成回滚。不能可靠保存或归属的情况提供手动复制后翻译。

## 验证

`--clipboard-self-test` 使用可控 AX、事件和 pasteboard，实现多格式/空内容恢复、拒绝备份、来源/焦点变化、用户复制、取消、超时、权限撤销、长文本与恢复失败检查，不访问系统剪贴板。

`--self-test --selection-fixture clipboard-slow` 在真实 Swift/C# 管道上模拟缓慢复制，验证显式启用、结构化结果、取消清理和 helper 后续可用性。它同样不发送真实键盘事件。

恢复 Mac 验收后，必须实际检查 Preview/浏览器/编辑器中的 Cmd+C、HID 输入计数是否正确区分 private 注入事件、单次 changeCount 规则、普通复制、富文本/图片原格式、权限撤销、取消/退出和延迟复制。CI 不能替代这些验收；不满足当前归属规则的应用先标记为需要手动复制。
