# 预览签名与钥匙串

主仓库使用长期复用的仓库专用自签名证书。公开证书为 `preview-signing.pem`；私钥不进入 Git，使用加密 PKCS#12 存储，导出密码由 Windows 当前用户 DPAPI 保护，保存在维护者本机的 `.codex/signing/YuWangSync-macOS/`。Actions 通过两个仓库 Secrets 导入临时钥匙串：

- `MACOS_SIGNING_CERTIFICATE_P12`：加密 PKCS#12 的 Base64。
- `MACOS_SIGNING_CERTIFICATE_PASSWORD`：PKCS#12 解密密码。

PKCS#12 导出需兼容 macOS 的 `security import`：使用 PBESv1 SHA-1 / 3-Key TripleDES-CBC、SHA-1 MAC 和高迭代次数，搭配随机高强度密码。重新封装容器时保留原私钥与证书，不改变签名身份。

应用、引擎及动态库使用固定标识和包含证书指纹的 designated requirement，后续更新复用同一身份。不要每次构建重新生成证书。更换证书会改变钥匙串识别的程序身份；丢失本机密钥时应从安全备份恢复原密钥。

已安装旧 ad-hoc 版本的用户换用此签名包时，旧钥匙串条目可能再次请求访问授权；应确认请求来自应用包中的 `YuWangSync.Engine` 后选择“始终允许”。获准读取后，程序复制到独立的 `credentials.v2` 服务条目，后续启动优先读取新条目，避免一直依赖旧 ad-hoc 条目的授权记录。旧条目保留且不修改访问控制；移除账号会删除该账号的新旧条目。CI 验证迁移后即使移除合成旧条目仍可读取新条目，以及同一版本多次启动可以保留新存凭据的访问权限。

当前免费自签名方案仍有边界：CI 实测，代码哈希变化后的新版本访问旧版本条目时仍需重新授权，不能承诺每次覆盖更新都完全无弹窗。回归保留这项检查，并验证新版本创建测试条目后多次启动不再请求授权；此测试没有模拟用户点击“始终允许”，旧条目的授权迁移仍需真机确认。不要删除现有钥匙串条目或把它改成允许所有程序访问。

自签名不等于 Apple Developer ID 签名或 Apple 公证。安装到另一台 Mac 时仍受 Gatekeeper 检查；应用不会向用户系统添加受信任根证书，也不会修改系统信任设置。仅签名用的临时 CI runner 导入证书；钥匙串回归使用另一台未导入证书或私钥的全新 runner。外部构建没有签名 Secrets 时回退为 ad-hoc，并跳过同身份更新测试；主仓库的非 PR 构建缺少 Secrets 则直接失败。
