using System;
using System.Text;
using PooledIO;
using BenchmarkDotNet;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Diagnosers;
using System.IO;
using System.Buffers;
using BenchmarkDotNet.Environments;

[Config(typeof(BenchmarkConfig))]
public class Program
{
    const int DataSizeMax = 100_000;
    [Params(100, 1_000, 10_000, DataSizeMax)]
    public int DataSize { get; set; }

    private byte[] Data = new byte[DataSizeMax];

    [Params(1000)]
    public int MaxLoop { get; set; }

    [Params(/*false,*/ true)]
    public bool PreAllocate { get; set; }

    public Program()
    {
        Data.AsSpan().Fill(0xCC);
    }

    [Benchmark(Baseline = true)]
    public void NormalStreamTest()
    {
        for (int i = 0; i < MaxLoop; i++)
        {
            using (var stm = new MemoryStream(PreAllocate ? DataSize : 1))
            {
                //stm.Write(Data, 0, DataSize);
                //stm.Read(Data, 0, DataSize);
            }
        }
    }

    [Benchmark]
    public void MemoryStreamWithPoolTest()
    {
        var buffer =  ArrayPool<byte>.Shared.Rent(DataSize);
        for (int i = 0; i < MaxLoop; i++)
        {
            using (var stm = new MemoryStream(buffer, 0, DataSize))
            {
                //stm.Write(Data, 0, DataSize);
                //stm.Read(Data, 0, DataSize);
            }
        }
        ArrayPool<byte>.Shared.Return(buffer);
    }

    [Benchmark]
    public void PooledStreamBench()
    {
        for (int i = 0; i < MaxLoop; i++)
        {
            using (var stm = new PooledStream.PooledMemoryStream(ArrayPool<byte>.Shared, PreAllocate ? DataSize : 1))
            {
                //stm.Write(Data, 0, DataSize);
                //stm.Read(Data, 0, DataSize);
            }
        }
    }
    [Benchmark]
    public void PooledArrayStreamBench()
    {
        for (int i = 0; i < MaxLoop; i++)
        {
            PooledArray<byte> pooledArray = default;
            try
            { 
                using (var stm = new PooledArrayStream(ArrayPool<byte>.Shared, PreAllocate ? DataSize : 1))
                {
                    //stm.Write(Data, 0, DataSize);
                    //stm.Read(Data, 0, DataSize);
                    //pooledArray = stm.DisposeAndGetMemory();
                }
            }
            finally
            {
                pooledArray.Dispose();
            }

        }
    }

    static void Main(string[] args)
    {
        var p = new Program() { DataSize = 100_000, MaxLoop = 2, PreAllocate = true, };
        p.MemoryStreamWithPoolTest();
        p.PooledArrayStreamBench();
        p.NormalStreamTest();
        p.PooledStreamBench();

        _ = BenchmarkSwitcher.FromTypes(new[] { typeof(Program) }).Run();
    }

}

internal class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        //AddJob(Job.ShortRun.
        //    WithWarmupCount(1).
        //    WithIterationCount(1).
        //    WithLaunchCount(5).WithRuntime(CoreRuntime.Core50));
        AddJob(Job.ShortRun.
            WithWarmupCount(1).
            WithIterationCount(1).
            WithLaunchCount(5).WithRuntime(ClrRuntime.Net48));
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}