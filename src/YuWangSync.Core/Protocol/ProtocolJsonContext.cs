using System.Text.Json.Serialization;
using YuWangCloudSync.Net;
namespace YuWangCloudSync;
[JsonSerializable(typeof(SessionCookie[]))]
internal partial class AppJsonSerializerContext : JsonSerializerContext { }
