namespace FeatureFlow.Providers.Abstractions.Tests;

/// <summary>The record kinds are declared once (docs/06 section 3.12): every list of a batch has exactly one declaration.</summary>
public sealed class RecordKindsTests
{
    [Fact]
    public void RecordKinds_cover_every_batch_list()
    {
        var constructor = typeof(RecordBatch).GetConstructors().Single();
        var parameters = constructor.GetParameters();

        foreach (var (parameter, index) in parameters.Select((p, i) => (p, i)))
        {
            // A batch holding only this list: exactly one declared kind reads it, and that kind's record type is the list's.
            var list = Array.CreateInstance(parameter.ParameterType.GetGenericArguments()[0], 0);
            var batch = (RecordBatch)constructor.Invoke([.. parameters.Select((_, i) => i == index ? list : null)]);

            var reading = RecordKinds.All.Where(k => ReferenceEquals(k.Of(batch), list)).ToList();
            Assert.True(reading.Count == 1, $"{parameter.Name} is read by {reading.Count} declared kinds");
            Assert.Equal(list.GetType().GetElementType(), reading[0].Record);
        }

        Assert.Equal(parameters.Length, RecordKinds.All.Count);
    }
}
