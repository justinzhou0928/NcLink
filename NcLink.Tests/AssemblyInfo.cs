using Xunit;

// 集成测试会启动真实的 MQTT 设备、并主动让设备掉线，多个测试类并行时会互相干扰
// （设备句柄、连接、超时窗口共享同一 Broker）。
// 因此整个程序集关闭并行执行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
