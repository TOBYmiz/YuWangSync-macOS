# 预览签名与钥匙串

主仓库使用长期复用的仓库专用自签名证书。公开证书为 `preview-signing.pem`；私钥不进入 Git，使用加密 PKCS#12 存储，导出密码由 Windows 当前用户 DPAPI 保护，保存在维护者本机的 `.codex/signing/YuWangSync-macOS/`。Actions 通过两个仓库 Secrets 导入临时钥匙串：

- `MACOS_SIGNING_CERTIFICATE_P12`：加密 PKCS#12 的 Base64。
- `MACOS_SIGNING_CERTIFICATE_PASSWORD`：PKCS#12 解密密码。

PKCS#12 导出需兼容 macOS 的 `security import`：使用 PBESv1 SHA-1 / 3-Key TripleDES-CBC、SHA-1 MAC 和高迭代次数，搭配随机高强度密码。重新封装容器时保留原私钥与证书，不改变签名身份。

应用、引擎及动态库使用固定标识和包含证书指纹的 designated requirement，后续更新复用同一身份。不要每次构建重新生成证书。更换证书会改变钥匙串识别的程序身份；丢失本机密钥时应从安全备份恢复原密钥。

已安装旧 ad-hoc 版本的用户首次换用此签名包时，旧钥匙串条目可能再次请求访问授权；应确认请求来自应用包中的 `YuWangSync.Engine` 后选择“始终允许”。之后的重启与同身份更新由跨进程 CI 测试覆盖，旧版本到新版本的系统弹窗及用户授权仍需真机验收。不要删除现有钥匙串条目或把它改成允许所有程序访问。

自签名不等于 Apple Developer ID 签名或 Apple 公证。安装到另一台 Mac 时仍受 Gatekeeper 检查；应用不会向用户系统添加受信任根证书，也不会修改系统信任设置。仅签名用的临时 CI runner 导入证书；钥匙串回归使用另一台未导入证书或私钥的全新 runner。外部构建没有签名 Secrets 时回退为 ad-hoc，并跳过同身份更新测试；主仓库的非 PR 构建缺少 Secrets 则直接失败。
