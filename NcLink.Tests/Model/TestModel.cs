using NcLink.Client.Devices.Engine;
namespace NcLink.Tests.Model;

/// <summary>测试用模型：结构参考官方 model.json，但只保留必要节点。</summary>
internal static class TestModel
{
    public const string DeviceId = "TEST_DEVICE_00001";

    public const string Json = """
    {
      "id": "01",
      "type": "NC_LINK_ROOT",
      "name": "机床模型文件",
      "devices": [
        {
          "id": "0103",
          "type": "MACHINE",
          "name": "数控机床",
          "configs": [
            {
              "id": "sample_channel0",
              "type": "SAMPLE_CHANNEL",
              "name": "采样通道",
              "sampleInterval": 1000,
              "uploadInterval": 1000,
              "ids": [ { "id": "010302" }, { "id": "01035430" } ]
            }
          ],
          "dataItems": [
            { "id": "010302", "type": "STATUS", "name": "机床状态" },
            { "id": "010303", "type": "FEED_SPEED", "name": "进给速度" }
          ],
          "components": [
            {
              "id": "010354",
              "type": "CONTROLLER",
              "name": "数控系统",
              "configs": [
                { "id": "01035407", "type": "PARAMETER", "name": "参数", "dataType": "LIST", "settable": true }
              ],
              "dataItems": [
                {
                  "id": "01035430",
                  "type": "VARIABLE",
                  "number": "REG_X",
                  "name": "寄存器X",
                  "dataType": "LIST",
                  "settable": true
                },
                { "id": "01035412", "type": "WARNING", "name": "报警" }
              ]
            }
          ]
        }
      ]
    }
    """;

    public static NcLink.Client.Devices.Models.DeviceModel Parse(string deviceId = DeviceId)
        => DeviceModelParser.Parse(Json, deviceId);
}
