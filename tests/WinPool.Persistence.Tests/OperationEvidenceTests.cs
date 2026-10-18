using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class OperationEvidenceTests
{
    [Fact]
    public async Task LargeSingleEvidenceHasAccurateSummaryAndRevisionBoundByteWindows()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPool.Evidence.Tests", Guid.NewGuid().ToString("N"));
        var store = new WinPoolSqliteStore(Path.Combine(directory, "winpool.db"));
        try
        {
            await store.InitializeAsync();
            var operationId = OperationId.New();
            var json = JsonSerializer.Serialize(new
            {
                samples = Enumerable.Range(0, 18_000).Select(index => new
                    { index, value = "引号\"反斜线\\" + new string('a', 80), code = "provider:" + index })
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var raw = Encoding.UTF8.GetBytes(json);
            await using (var connection = await store.OpenConnectionAsync())
            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO operation_plans(operation_id,plan_hash,environment_id,risk,state,sanitized_json,created_at_utc_ms)
                    VALUES($id,'exact-plan','environment',4,11,'{}',1);
                    INSERT INTO operation_steps(operation_id,step_id,sequence_no,state,sanitized_json,target_json,evidence_json)
                    VALUES($id,'step',0,7,'{}','{"target":"exact"}',$evidence);
                    INSERT INTO execution_events(operation_id,timestamp_utc_ms,kind,code,sanitized_message)
                    VALUES($id,1,1,'unknown','step');
                    """;
                insert.Parameters.AddWithValue("$id", operationId.Value.ToString("N"));
                insert.Parameters.AddWithValue("$evidence", json);
                await insert.ExecuteNonQueryAsync();
            }
            var repository = new OperationPlanRepository(store);
            var page = await repository.ReadEvidencePageAsync(operationId, 0, 16, CancellationToken.None);
            var step = Assert.Single(page.Steps);
            Assert.Equal("{\"target\":\"exact\"}", step.TargetJson);
            Assert.Null(step.EvidenceJson);
            var reference = Assert.Single(page.References, value => value.Field == RealEvidenceField.Result);
            Assert.False(reference.InlineComplete);
            Assert.Equal(raw.Length, reference.TotalBytes);
            await using var reconstructed = new MemoryStream();
            long offset = 0;
            do
            {
                var window = await repository.ReadEvidenceWindowAsync(operationId, "step", RealEvidenceField.Result,
                    offset, 123_457, reference.Revision, CancellationToken.None);
                Assert.NotNull(window);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(window.Bytes)).ToLowerInvariant(), window.Sha256);
                Assert.Equal(raw.Length, window.TotalBytes);
                await reconstructed.WriteAsync(window.Bytes);
                if (window.NextOffset is not { } next) break;
                offset = next;
            } while (true);
            Assert.Equal(raw, reconstructed.ToArray());
            await using (var connection = await store.OpenConnectionAsync())
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = "INSERT INTO execution_events(operation_id,timestamp_utc_ms,kind,code,sanitized_message) VALUES($id,2,1,'changed','step');";
                update.Parameters.AddWithValue("$id", operationId.Value.ToString("N"));
                await update.ExecuteNonQueryAsync();
            }
            Assert.Null(await repository.ReadEvidenceWindowAsync(operationId, "step", RealEvidenceField.Result,
                0, 1, reference.Revision, CancellationToken.None));
        }
        finally
        {
            store.DrainConnectionPool();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
