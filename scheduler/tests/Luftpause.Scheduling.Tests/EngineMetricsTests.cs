namespace Luftpause.Scheduling.Tests;

public class EngineMetricsTests
{
    [Fact]
    public void Sglang_running_and_queued_requests_are_summed_across_label_sets()
    {
        const string text = """
            # HELP sglang:num_running_reqs The number of running requests.
            # TYPE sglang:num_running_reqs gauge
            sglang:num_running_reqs{model_name="m",tp_rank="0"} 5.0
            sglang:num_running_reqs{model_name="m",tp_rank="1"} 2.0
            sglang:num_queue_reqs{model_name="m"} 3.0
            sglang:token_usage{model_name="m"} 0.91
            """;

        Assert.Equal(new EngineLoadSample(7, 3), EngineMetrics.Parse(text));
    }

    [Fact]
    public void Per_priority_rows_are_a_breakdown_of_the_total_and_are_not_added_again()
    {
        const string text = """
            sglang:num_running_reqs{engine_type="unified",priority="",tp_rank="0"} 17.0
            sglang:num_running_reqs{engine_type="unified",priority="-9223372036854775808",tp_rank="0"} 17.0
            sglang:num_queue_reqs{engine_type="unified",priority="",tp_rank="0"} 11.0
            sglang:num_queue_reqs{engine_type="unified",priority="100",tp_rank="0"} 4.0
            sglang:num_queue_reqs{engine_type="unified",priority="0",tp_rank="0"} 7.0
            """;

        Assert.Equal(new EngineLoadSample(17, 11), EngineMetrics.Parse(text));
    }

    [Fact]
    public void Vllm_running_and_waiting_requests_are_read()
    {
        const string text = """
            vllm:num_requests_running{engine="0",model_name="m"} 12.0
            vllm:num_requests_waiting{engine="0",model_name="m"} 0.0
            """;

        Assert.Equal(new EngineLoadSample(12, 0), EngineMetrics.Parse(text));
    }

    [Fact]
    public void Metrics_without_a_queue_report_say_nothing()
    {
        Assert.Null(EngineMetrics.Parse("process_cpu_seconds_total 1.5\nsglang:num_running_reqs 2"));
        Assert.Null(EngineMetrics.Parse(""));
    }
}
