using System.Reflection;
using BenchmarkDotNet.Running;

// Run with:  dotnet run -c Release --project bench/Monitor.Benchmarks -- --filter '*'
// Or a single class:  ... -- --filter '*FrameBenchmarks*'
BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
