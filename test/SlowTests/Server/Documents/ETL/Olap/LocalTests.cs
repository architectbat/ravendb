﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FastTests.Client;
using FastTests.Server.Basic.Entities;
using Newtonsoft.Json;
using Parquet;
using Parquet.Data;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations.Backups;
using Raven.Client.Documents.Operations.ETL;
using Raven.Client.Documents.Operations.ETL.OLAP;
using Raven.Client.ServerWide.Operations;
using Raven.Server.Documents.ETL.Providers.OLAP;
using Sparrow.Platform;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Server.Documents.ETL.Olap
{
    public class LocalTests : EtlTestBase
    {
        internal const string DefaultFrequency = "* * * * *"; // every minute

        public LocalTests(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public Task SimpleTransformation()
        {
            var script = @"
var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var key = new Date(year, month);

loadToOrders(partitionBy(key),
    {
        Company : this.Company,
        ShipVia : this.ShipVia
    });
";
            return SimpleTransformationInternal(script);
        }

        [Fact]
        public Task SimpleTransformation_DifferentLoadTo_Syntax1()
        {
            var script = @"
var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var key = new Date(year, month);

loadTo('Orders', partitionBy(key),
    {
        Company : this.Company,
        ShipVia : this.ShipVia
    });
";

            return SimpleTransformationInternal(script);
        }

        [Fact]
        public Task SimpleTransformation_DifferentLoadTo_Syntax2()
        {
            var script = @"
var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var key = new Date(year, month);

loadTo(""Orders"", partitionBy(key),
    {
        Company : this.Company,
        ShipVia : this.ShipVia
    });
";

            return SimpleTransformationInternal(script);
        }

        private async Task SimpleTransformationInternal(string script)
        {
            var path = GetTempPath("Orders");
            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = new DateTime(2020, 1, 1);

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 0; i < 31; i++)
                        {
                            await session.StoreAsync(new Query.Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = baseline.AddDays(i),
                                ShipVia = $"shippers/{i}",
                                Company = $"companies/{i}"
                            });
                        }

                        for (int i = 0; i < 28; i++)
                        {
                            await session.StoreAsync(new Query.Order
                            {
                                Id = $"orders/{i + 31}",
                                OrderedAt = baseline.AddMonths(1).AddDays(i),
                                ShipVia = $"shippers/{i + 31}",
                                Company = $"companies/{i + 31}"
                            });
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    SetupLocalOlapEtl(store, script, path);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    var files = Directory.GetFiles(path);
                    Assert.Equal(2, files.Length);

                    var expectedFields = new[] { "ShipVia", "Company", ParquetTransformedItems.DefaultIdColumn, ParquetTransformedItems.LastModifiedColumn };

                    foreach (var fileName in files)
                    {
                        using (var fs = File.OpenRead(fileName))
                        using (var parquetReader = new ParquetReader(fs))
                        {
                            Assert.Equal(1, parquetReader.RowGroupCount);
                            Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                            using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                            foreach (var field in parquetReader.Schema.Fields)
                            {
                                Assert.True(field.Name.In(expectedFields));
                                var data = rowGroupReader.ReadColumn((DataField)field).Data;

                                Assert.True(data.Length == 31 || data.Length == 28);
                                var count = data.Length == 31 ? 0 : 31;

                                if (field.Name == ParquetTransformedItems.LastModifiedColumn)
                                    continue;

                                foreach (var val in data)
                                {
                                    var expected = field.Name switch
                                    {
                                        ParquetTransformedItems.DefaultIdColumn => $"orders/{count}",
                                        "Company" => $"companies/{count}",
                                        "ShipVia" => $"shippers/{count}",
                                        _ => null
                                    };

                                    Assert.Equal(expected, val);
                                    count++;
                                }

                            }

                        }

                    }
                }

            }
            finally
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles())
                {
                    file.Delete();
                }

                di.Delete();
            }
        }

        [Fact]
        public async Task SimpleTransformation2()
        {
            var path = GetTempPath("Orders");
            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = new DateTime(2020, 1, 1);

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 1; i <= 10; i++)
                        {
                            var orderedAt = baseline.AddDays(i);
                            var o = new Query.Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = orderedAt,
                                RequireAt = orderedAt.AddDays(7),
                                Lines = new List<OrderLine>
                                {
                                    new OrderLine
                                    {
                                        Quantity = i * 10,
                                        PricePerUnit = (decimal)1.25,
                                    }
                                }
                            };

                            await session.StoreAsync(o);
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
var o = {
    RequireAt : new Date(this.RequireAt)
    Total : 0
};

for (var j = 0; j < this.Lines.length; j++)
{
    var line = this.Lines[j];
    var p = line.Quantity * line.PricePerUnit;
    o.Total += p;
}

var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var key = new Date(year, month);

loadToOrders(partitionBy(key), o);
";

                    SetupLocalOlapEtl(store, script, path);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    var files = Directory.GetFiles(path);
                    Assert.Equal(1, files.Length);

                    var expectedFields = new[] { "RequireAt", "Total", ParquetTransformedItems.DefaultIdColumn, ParquetTransformedItems.LastModifiedColumn };

                    using (var fs = File.OpenRead(files[0]))
                    using (var parquetReader = new ParquetReader(fs))
                    {
                        Assert.Equal(1, parquetReader.RowGroupCount);
                        Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                        using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                        foreach (var field in parquetReader.Schema.Fields)
                        {
                            Assert.True(field.Name.In(expectedFields));

                            var data = rowGroupReader.ReadColumn((DataField)field).Data;
                            Assert.True(data.Length == 10);

                            if (field.Name == ParquetTransformedItems.LastModifiedColumn)
                                continue;

                            long count = 1;
                            foreach (var val in data)
                            {
                                switch (field.Name)
                                {
                                    case ParquetTransformedItems.DefaultIdColumn:
                                        Assert.Equal($"orders/{count}", val);
                                        break;
                                    case "RequireAt":
                                        var expected = new DateTimeOffset(DateTime.SpecifyKind(baseline.AddDays(count).AddDays(7), DateTimeKind.Utc));
                                        Assert.Equal(expected, val);
                                        break;
                                    case "Total":
                                        var expectedTotal = count * 1.25M * 10;
                                        Assert.Equal(expectedTotal, val);
                                        break;
                                }

                                count++;

                            }
                        }
                    }
                }

            }
            finally
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles())
                {
                    file.Delete();
                }

                di.Delete();
            }
        }

        [Fact]
        public async Task SimpleTransformation_PartitionByDay()
        {
            var path = GetTempPath("Orders");
            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = new DateTime(2020, 1, 1);

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 0; i < TimeSpan.FromDays(7).TotalHours; i++)
                        {
                            var orderedAt = baseline.AddHours(i);
                            var o = new Query.Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = orderedAt,
                                RequireAt = orderedAt.AddDays(7),
                                Lines = new List<OrderLine>
                                {
                                    new OrderLine
                                    {
                                        Quantity = i * 10,
                                        PricePerUnit = i
                                    }
                                }
                            };

                            await session.StoreAsync(o);
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
var o = {
    RequireAt : new Date(this.RequireAt)
    Total : 0
};

for (var j = 0; j < this.Lines.length; j++)
{
    var line = this.Lines[j];
    var p = line.Quantity * line.PricePerUnit;
    o.Total += p;
}

var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var day = orderDate.getDay();
var key = new Date(year, month, day);

loadToOrders(partitionBy(key), o);
";

                    SetupLocalOlapEtl(store, script, path);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    var files = Directory.GetFiles(path);
                    Assert.Equal(7, files.Length);

                    var expectedFields = new[] { "RequireAt", "Total", ParquetTransformedItems.DefaultIdColumn, ParquetTransformedItems.LastModifiedColumn };

                    foreach (var file in files)
                    {
                        using (var fs = File.OpenRead(files[0]))
                        using (var parquetReader = new ParquetReader(fs))
                        {
                            Assert.Equal(1, parquetReader.RowGroupCount);
                            Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                            using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                            foreach (var field in parquetReader.Schema.Fields)
                            {
                                Assert.True(field.Name.In(expectedFields));

                                var data = rowGroupReader.ReadColumn((DataField)field).Data;
                                Assert.True(data.Length == 24);
                            }
                        }
                    }
                }
            }
            finally
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles())
                {
                    file.Delete();
                }

                di.Delete();
            }
        }

        [Fact]
        public async Task SimpleTransformation_PartitionByHour()
        {
            var path = GetTempPath("Orders");
            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = new DateTime(2020, 1, 1);

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 0; i < TimeSpan.FromDays(1).TotalMinutes; i++)
                        {
                            var orderedAt = baseline.AddMinutes(i);
                            var o = new Query.Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = orderedAt,
                                RequireAt = orderedAt.AddDays(7),
                                Lines = new List<OrderLine>
                                {
                                    new OrderLine
                                    {
                                        Quantity = i * 10,
                                        PricePerUnit = i
                                    }
                                }
                            };

                            await session.StoreAsync(o);
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
var o = {
    RequireAt : new Date(this.RequireAt)
    Total : 0
};

for (var j = 0; j < this.Lines.length; j++)
{
    var line = this.Lines[j];
    var p = line.Quantity * line.PricePerUnit;
    o.Total += p;
}

var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var day = orderDate.getDay();
var hour = orderDate.getHours();
var key = new Date(year, month, day, hour);

loadToOrders(partitionBy(key), o);
";

                    SetupLocalOlapEtl(store, script, path);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    var files = Directory.GetFiles(path);
                    Assert.Equal(24, files.Length);

                    var expectedFields = new[] { "RequireAt", "Total", ParquetTransformedItems.DefaultIdColumn, ParquetTransformedItems.LastModifiedColumn };

                    foreach (var file in files)
                    {
                        using (var fs = File.OpenRead(files[0]))
                        using (var parquetReader = new ParquetReader(fs))
                        {
                            Assert.Equal(1, parquetReader.RowGroupCount);
                            Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                            using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                            foreach (var field in parquetReader.Schema.Fields)
                            {
                                Assert.True(field.Name.In(expectedFields));

                                var data = rowGroupReader.ReadColumn((DataField)field).Data;
                                Assert.True(data.Length == 60);
                            }
                        }
                    }
                }
            }
            finally
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles())
                {
                    file.Delete();
                }

                di.Delete();
            }
        }

        [Fact]
        public async Task CanHandleMissingFieldsOnSomeDocuments()
        {
            var path = GetTempPath("Orders");
            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = new DateTime(2020, 1, 1);

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 1; i <= 10; i++)
                        {
                            var o = new Query.Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = baseline.AddDays(i),
                                Company = $"companies/{i}"
                            };

                            if (i % 2 == 0)
                                o.Freight = i;

                            await session.StoreAsync(o);
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);


                    var script = @"
var o = {
    Company : this.Company
};

if (this.Freight > 0)
    o.Freight = this.Freight

var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var key = new Date(year, month);

loadToOrders(partitionBy(key), o);
";

                    SetupLocalOlapEtl(store, script, path);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    var files = Directory.GetFiles(path);
                    Assert.Equal(1, files.Length);

                    var expectedFields = new[] { "Company", "Freight", ParquetTransformedItems.DefaultIdColumn, ParquetTransformedItems.LastModifiedColumn };

                    using (var fs = File.OpenRead(files[0]))
                    using (var parquetReader = new ParquetReader(fs))
                    {
                        Assert.Equal(1, parquetReader.RowGroupCount);
                        Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                        using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                        foreach (var field in parquetReader.Schema.Fields)
                        {
                            Assert.True(field.Name.In(expectedFields));

                            var data = rowGroupReader.ReadColumn((DataField)field).Data;
                            Assert.True(data.Length == 10);

                            if (field.Name == ParquetTransformedItems.LastModifiedColumn)
                                continue;

                            var count = 1;
                            foreach (var val in data)
                            {
                                switch (field.Name)
                                {
                                    case ParquetTransformedItems.DefaultIdColumn:
                                        Assert.Equal($"orders/{count}", val);
                                        break;
                                    case "Company":
                                        Assert.Equal($"companies/{count}", val);
                                        break;
                                    case "Freight":
                                        long expected = count % 2 == 0 ? count : 0;
                                        Assert.Equal(expected, val);
                                        break;
                                }

                                count++;

                            }
                        }
                    }
                }

            }
            finally
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles())
                {
                    file.Delete();
                }

                di.Delete();
            }
        }

        [Fact]
        public async Task CanHandleNullFieldValuesOnSomeDocument()
        {
            var path = GetTempPath("Orders");
            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = new DateTime(2020, 1, 1);

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 1; i <= 10; i++)
                        {
                            var o = new Query.Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = baseline.AddDays(i),
                            };

                            if (i % 2 == 0)
                                o.Company = "companies/" + i;

                            await session.StoreAsync(o);
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
var o = {
    Company : this.Company
};

var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var key = new Date(year, month);

loadToOrders(partitionBy(key), o);
";

                    SetupLocalOlapEtl(store, script, path);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    var files = Directory.GetFiles(path);
                    Assert.Equal(1, files.Length);

                    var expectedFields = new[] { "Company", ParquetTransformedItems.DefaultIdColumn, ParquetTransformedItems.LastModifiedColumn };

                    using (var fs = File.OpenRead(files[0]))
                    using (var parquetReader = new ParquetReader(fs))
                    {
                        Assert.Equal(1, parquetReader.RowGroupCount);
                        Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                        using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                        foreach (var field in parquetReader.Schema.Fields)
                        {
                            Assert.True(field.Name.In(expectedFields));

                            var data = rowGroupReader.ReadColumn((DataField)field).Data;
                            Assert.True(data.Length == 10);

                            if (field.Name == ParquetTransformedItems.LastModifiedColumn)
                                continue;

                            var count = 1;
                            foreach (var val in data)
                            {
                                switch (field.Name)
                                {
                                    case ParquetTransformedItems.DefaultIdColumn:
                                        Assert.Equal($"orders/{count}", val);
                                        break;
                                    case "Company":
                                        string expected = count % 2 == 0 ? $"companies/{count}" : null;
                                        Assert.Equal(expected, val);
                                        break;
                                }

                                count++;

                            }
                        }
                    }
                }

            }
            finally
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles())
                {
                    file.Delete();
                }

                di.Delete();
            }
        }

        [Fact]
        public async Task ShouldRespectEtlRunFrequency()
        {
            var path = GetTempPath("Orders");
            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = new DateTime(2020, 1, 1);

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 1; i <= 10; i++)
                        {
                            var o = new Query.Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = baseline.AddDays(i),
                                Company = $"companies/{i}"
                            };

                            await session.StoreAsync(o);
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
var o = {
    Company : this.Company
};

var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var key = new Date(year, month);

loadToOrders(partitionBy(key), o);
";

                    var frequency = DateTime.UtcNow.Minute % 2 == 1
                        ? "1-59/2 * * * *" // every uneven minute
                        : "*/2 * * * *"; // every 2nd minute (even minutes)

                    SetupLocalOlapEtl(store, script, path, frequency: frequency);
                    etlDone.Wait(TimeSpan.FromMinutes(1));
                    var sw = new Stopwatch();
                    sw.Start();

                    var files = Directory.GetFiles(path);
                    Assert.Equal(1, files.Length);

                    var expectedFields = new[] { "Company", ParquetTransformedItems.DefaultIdColumn, ParquetTransformedItems.LastModifiedColumn };

                    using (var fs = File.OpenRead(files[0]))
                    using (var parquetReader = new ParquetReader(fs))
                    {
                        Assert.Equal(1, parquetReader.RowGroupCount);
                        Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                        using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                        foreach (var field in parquetReader.Schema.Fields)
                        {
                            Assert.True(field.Name.In(expectedFields));

                            var data = rowGroupReader.ReadColumn((DataField)field).Data;
                            Assert.True(data.Length == 10);

                            if (field.Name == ParquetTransformedItems.LastModifiedColumn)
                                continue;

                            var count = 1;
                            foreach (var val in data)
                            {
                                switch (field.Name)
                                {
                                    case ParquetTransformedItems.DefaultIdColumn:
                                        Assert.Equal($"orders/{count}", val);
                                        break;
                                    case "Company":
                                        Assert.Equal($"companies/{count}", val);
                                        break;
                                }

                                count++;

                            }
                        }
                    }

                    await Task.Delay(1000);

                    baseline = new DateTime(2021, 1, 1);

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 20; i <= 30; i++)
                        {
                            var o = new Query.Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = baseline.AddDays(i),
                                Company = $"companies/{i}"
                            };

                            await session.StoreAsync(o);
                        }

                        await session.SaveChangesAsync();
                    }

                    etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);
                    etlDone.Wait(TimeSpan.FromSeconds(120));
                    var timeWaited = sw.Elapsed.TotalMilliseconds;
                    sw.Stop();

                    Assert.True(timeWaited > TimeSpan.FromSeconds(60).TotalMilliseconds);

                    files = Directory.GetFiles(path);
                    Assert.Equal(2, files.Length);
                }

            }
            finally
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles())
                {
                    file.Delete();
                }

                di.Delete();
            }
        }

        [Fact]
        public async Task CanUseSettingFromScript()
        {
            var path = GetTempPath("Orders");
            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = new DateTime(2020, 1, 1);

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 1; i <= 10; i++)
                        {
                            var orderedAt = baseline.AddDays(i);
                            var o = new Query.Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = orderedAt,
                                RequireAt = orderedAt.AddDays(7),
                                Lines = new List<OrderLine>
                                {
                                    new OrderLine
                                    {
                                        Quantity = i * 10,
                                        PricePerUnit = i
                                    }
                                }
                            };

                            await session.StoreAsync(o);
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var transformationScript = @"
var o = {
    RequireAt : new Date(this.RequireAt)
    Total : 0
};

for (var j = 0; j < this.Lines.length; j++)
{
    var line = this.Lines[j];
    var p = line.Quantity * line.PricePerUnit;
    o.Total += p;
}

var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var key = new Date(year, month);

loadToOrders(partitionBy(key), o);
";

                    var connectionStringName = $"{store.Database} to S3";

                    var scriptPath = GenerateConfigurationScript(path, out string command);
                    var connectionString = new OlapConnectionString
                    {
                        Name = connectionStringName,
                        LocalSettings = new LocalSettings
                        {
                            GetBackupConfigurationScript = new GetBackupConfigurationScript
                            {
                                Exec = command,
                                Arguments = scriptPath
                            }
                        }
                    };

                    var configuration = new OlapEtlConfiguration
                    {
                        Name = connectionStringName,
                        ConnectionStringName = connectionStringName,
                        RunFrequency = DefaultFrequency,
                        Transforms =
                        {
                            new Transformation
                            {
                                Name = "MonthlyOrders",
                                Collections = new List<string> {"Orders"},
                                Script = transformationScript
                            }
                        }
                    };

                    AddEtl(store, configuration, connectionString);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    var files = Directory.GetFiles(path);
                    Assert.Equal(1, files.Length);
                }
            }
            finally
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles())
                {
                    file.Delete();
                }

                di.Delete();
            }
        }

        [Fact]
        public async Task AfterDatabaseRestartEtlShouldRespectRunFrequency()
        {
            var path = GetTempPath("Orders");
            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = new DateTime(2020, 1, 1);

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 1; i <= 10; i++)
                        {
                            var o = new Query.Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = baseline.AddDays(i),
                                Company = $"companies/{i}"
                            };

                            await session.StoreAsync(o);
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
var o = {
    Company : this.Company
};

var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var key = new Date(year, month);

loadToOrders(partitionBy(key), o);
";

                    var sw = new Stopwatch();
                    sw.Start();
                    SetupLocalOlapEtl(store, script, path, frequency: "*/3 * * * *"); // every 3rd minute
                    Assert.True(etlDone.Wait(TimeSpan.FromMinutes(1)));

                    var files = Directory.GetFiles(path);
                    Assert.Equal(1, files.Length);

                    // disable and re enable the database

                    var result = store.Maintenance.Server.Send(new ToggleDatabasesStateOperation(store.Database, disable: true));
                    Assert.True(result.Success);
                    Assert.True(result.Disabled);

                    result = store.Maintenance.Server.Send(new ToggleDatabasesStateOperation(store.Database, disable: false));
                    Assert.True(result.Success);
                    Assert.False(result.Disabled);

                    etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    baseline = new DateTime(2021, 1, 1);

                    // add more data
                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 20; i <= 30; i++)
                        {
                            var o = new Query.Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = baseline.AddDays(i),
                                Company = $"companies/{i}"
                            };

                            await session.StoreAsync(o);
                        }

                        await session.SaveChangesAsync();
                    }

                    Assert.False(etlDone.Wait(TimeSpan.FromSeconds(50)));
                    files = Directory.GetFiles(path);
                    Assert.Equal(1, files.Length);

                    Assert.True(etlDone.Wait(TimeSpan.FromSeconds(180)));

                    var timeWaited = sw.Elapsed.TotalMilliseconds;
                    sw.Stop();

                    Assert.True(timeWaited > TimeSpan.FromSeconds(60).TotalMilliseconds, "time : " + sw.Elapsed);

                    files = Directory.GetFiles(path);
                    Assert.Equal(2, files.Length);
                }

            }
            finally
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles())
                {
                    file.Delete();
                }

                di.Delete();
            }
        }

        [Fact]
        public async Task LastModifiedTicksShouldMatch()
        {
            var path = GetTempPath("Orders");
            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = new DateTime(2020, 1, 1);

                    var ids = new List<string>();

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 1; i <= 10; i++)
                        {
                            var orderedAt = baseline.AddDays(i);
                            var id = $"orders/{i}";
                            ids.Add(id);
                            var o = new Query.Order
                            {
                                Id = id,
                                OrderedAt = orderedAt,
                                RequireAt = orderedAt.AddDays(7),
                                Lines = new List<OrderLine>
                                {
                                    new OrderLine
                                    {
                                        Quantity = i * 10,
                                        PricePerUnit = i
                                    }
                                }
                            };

                            await session.StoreAsync(o);
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
var o = {
    RequireAt : new Date(this.RequireAt)
};

var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var key = new Date(year, month);

loadToOrders(partitionBy(key), o);
";

                    SetupLocalOlapEtl(store, script, path);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    var files = Directory.GetFiles(path);
                    Assert.Equal(1, files.Length);

                    var expectedFields = new[] { "RequireAt", ParquetTransformedItems.DefaultIdColumn, ParquetTransformedItems.LastModifiedColumn };

                    using (var session = store.OpenAsyncSession())
                    {
                        var docs = await session.LoadAsync<Order>(ids.ToArray());
                        string[] idFieldData = null;
                        long?[] lsatModifiedFieldData = null;

                        using (var fs = File.OpenRead(files[0]))
                        using (var parquetReader = new ParquetReader(fs))
                        {
                            Assert.Equal(1, parquetReader.RowGroupCount);
                            Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                            using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                            foreach (var field in parquetReader.Schema.Fields)
                            {
                                Assert.True(field.Name.In(expectedFields));

                                var data = rowGroupReader.ReadColumn((DataField)field).Data;
                                Assert.True(data.Length == 10);

                                switch (field.Name)
                                {
                                    case ParquetTransformedItems.LastModifiedColumn:
                                        lsatModifiedFieldData = (long?[])data;
                                        break;
                                    case ParquetTransformedItems.DefaultIdColumn:
                                        idFieldData = (string[])data;
                                        break;
                                    case "RequireAt":
                                        continue;
                                }
                            }
                        }

                        Assert.NotNull(idFieldData);
                        Assert.NotNull(lsatModifiedFieldData);

                        for (var index = 0; index < idFieldData.Length; index++)
                        {
                            var id = idFieldData[index];
                            Assert.True(docs.TryGetValue(id, out var doc));

                            var expectedTicks = session.Advanced.GetLastModifiedFor(doc)?.Ticks;
                            Assert.Equal(expectedTicks, lsatModifiedFieldData[index]);
                        }
                    }

                }

            }
            finally
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles())
                {
                    file.Delete();
                }

                di.Delete();
            }
        }

        [Fact]
        public async Task CanModifyIdColumnName()
        {
            var path = GetTempPath("Orders");
            try
            {
                using (var store = GetDocumentStore())
                {
                    const string idColumn = "OrderId";

                    var baseline = new DateTime(2020, 1, 1);

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 1; i <= 10; i++)
                        {
                            var orderedAt = baseline.AddDays(i);
                            var o = new Query.Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = orderedAt,
                                RequireAt = orderedAt.AddDays(7),
                                Lines = new List<OrderLine>
                                {
                                    new OrderLine
                                    {
                                        Quantity = i * 10,
                                        PricePerUnit = i
                                    }
                                }
                            };

                            await session.StoreAsync(o);
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
var o = {
    RequireAt : new Date(this.RequireAt)
    Total : 0
};

for (var j = 0; j < this.Lines.length; j++)
{
    var line = this.Lines[j];
    var p = line.Quantity * line.PricePerUnit;
    o.Total += p;
}

var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var key = new Date(year, month);

loadToOrders(partitionBy(key), o);
";

                    var connectionStringName = $"{store.Database} to local";
                    var configuration = new OlapEtlConfiguration
                    {
                        Name = "olap-test",
                        ConnectionStringName = connectionStringName,
                        RunFrequency = DefaultFrequency,
                        Transforms =
                        {
                            new Transformation
                            {
                                Name = "MonthlyOrders",
                                Collections = new List<string> {"Orders"},
                                Script = script
                            }
                        },
                        OlapTables = new List<OlapEtlTable>()
                        {
                            new OlapEtlTable
                            {
                                TableName = "Orders",
                                DocumentIdColumn = idColumn
                            }
                        }
                    };


                    SetupLocalOlapEtl(store, configuration, path, connectionStringName);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    var files = Directory.GetFiles(path);
                    Assert.Equal(1, files.Length);

                    var expectedFields = new[] { "RequireAt", "Total", idColumn, ParquetTransformedItems.LastModifiedColumn };

                    using (var fs = File.OpenRead(files[0]))
                    using (var parquetReader = new ParquetReader(fs))
                    {
                        Assert.Equal(1, parquetReader.RowGroupCount);
                        Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                        using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                        foreach (var field in parquetReader.Schema.Fields)
                        {
                            Assert.True(field.Name.In(expectedFields));

                            var data = rowGroupReader.ReadColumn((DataField)field).Data;
                            Assert.True(data.Length == 10);

                            if (field.Name == ParquetTransformedItems.LastModifiedColumn)
                                continue;

                            long count = 1;
                            foreach (var val in data)
                            {
                                switch (field.Name)
                                {
                                    case idColumn:
                                        Assert.Equal($"orders/{count}", val);
                                        break;
                                    case "RequireAt":
                                        var expected = new DateTimeOffset(DateTime.SpecifyKind(baseline.AddDays(count).AddDays(7), DateTimeKind.Utc));
                                        Assert.Equal(expected, val);
                                        break;
                                    case "Total":
                                        var expectedTotal = count * count * 10;
                                        Assert.Equal(expectedTotal, val);
                                        break;
                                }

                                count++;

                            }
                        }
                    }
                }

            }
            finally
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles())
                {
                    file.Delete();
                }

                di.Delete();
            }
        }

        [Fact]
        public async Task SimpleTransformation_NoPartition()
        {
            var path = GetTempPath("Orders");
            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = new DateTime(2020, 1, 1).ToUniversalTime();

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 0; i < 100; i++)
                        {
                            await session.StoreAsync(new Query.Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = baseline.AddDays(i),
                                ShipVia = $"shippers/{i}",
                                Company = $"companies/{i}"
                            });
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
loadToOrders(noPartition(),
    {
        OrderDate : this.OrderedAt
        Company : this.Company,
        ShipVia : this.ShipVia
    });
";
                    SetupLocalOlapEtl(store, script, path);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    var files = Directory.GetFiles(path);
                    Assert.Equal(1, files.Length);

                    var expectedFields = new[] { "OrderDate", "ShipVia", "Company", ParquetTransformedItems.DefaultIdColumn, ParquetTransformedItems.LastModifiedColumn };

                    foreach (var fileName in files)
                    {
                        using (var fs = File.OpenRead(fileName))
                        using (var parquetReader = new ParquetReader(fs))
                        {
                            Assert.Equal(1, parquetReader.RowGroupCount);
                            Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                            using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                            foreach (var field in parquetReader.Schema.Fields)
                            {
                                Assert.True(field.Name.In(expectedFields));
                                var data = rowGroupReader.ReadColumn((DataField)field).Data;

                                Assert.True(data.Length == 100);

                                if (field.Name == ParquetTransformedItems.LastModifiedColumn)
                                    continue;

                                var count = 0;
                                foreach (var val in data)
                                {
                                    if (field.Name == "OrderDate")
                                    {
                                        var expectedDto = new DateTimeOffset(DateTime.SpecifyKind(baseline.AddDays(count), DateTimeKind.Utc));
                                        Assert.Equal(expectedDto, val);
                                    }

                                    else
                                    {
                                        var expected = field.Name switch
                                        {
                                            ParquetTransformedItems.DefaultIdColumn => $"orders/{count}",
                                            "Company" => $"companies/{count}",
                                            "ShipVia" => $"shippers/{count}",
                                            _ => null
                                        };

                                        Assert.Equal(expected, val);
                                    }

                                    count++;
                                }

                            }

                        }

                    }
                }

            }
            finally
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles())
                {
                    file.Delete();
                }

                di.Delete();
            }
        }

        [Fact]
        public async Task SimpleTransformation_MultiplePartitions()
        {
            var path = GetTempPath("Orders");
            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = DateTime.SpecifyKind(new DateTime(2020, 1, 1), DateTimeKind.Utc);

                    using (var session = store.OpenAsyncSession())
                    {
                        const int total = 31 + 28; // days in January + days in February 

                        for (int i = 0; i < total; i++)
                        {
                            var orderedAt = baseline.AddDays(i);
                            await session.StoreAsync(new Query.Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = orderedAt,
                                RequireAt = orderedAt.AddDays(7),
                                ShipVia = $"shippers/{i}",
                                Company = $"companies/{i}"
                            });
                        }

                        for (int i = 1; i <= 37; i++)
                        {
                            var index = i + total;
                            var orderedAt = baseline.AddYears(1).AddMonths(1).AddDays(i);
                            await session.StoreAsync(new Query.Order
                            {
                                Id = $"orders/{index}",
                                OrderedAt = orderedAt,
                                RequireAt = orderedAt.AddDays(7),
                                ShipVia = $"shippers/{index}",
                                Company = $"companies/{index}"
                            });
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
var orderDate = new Date(this.OrderedAt);

loadToOrders(partitionBy([
    ['year', orderDate.getFullYear()],
    ['month', orderDate.getMonth()]
]),
    {
        Company : this.Company,
        ShipVia : this.ShipVia,
        RequireAt : this.RequireAt
    });
";
                    SetupLocalOlapEtl(store, script, path);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    var files = Directory.GetFiles(path);
                    Assert.Equal(4, files.Length);

                    var expectedFields = new[] { "RequireAt", "ShipVia", "Company", ParquetTransformedItems.DefaultIdColumn, ParquetTransformedItems.LastModifiedColumn };

                    foreach (var fileName in files)
                    {
                        using (var fs = File.OpenRead(fileName))
                        using (var parquetReader = new ParquetReader(fs))
                        {
                            Assert.Equal(1, parquetReader.RowGroupCount);
                            Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                            using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                            foreach (var field in parquetReader.Schema.Fields)
                            {
                                Assert.True(field.Name.In(expectedFields));
                                var data = rowGroupReader.ReadColumn((DataField)field).Data;

                                Assert.True(data.Length == 31 || data.Length == 28 || data.Length == 27 || data.Length == 10);
                                if (field.Name != "RequireAt")
                                    continue;

                                var count = data.Length switch
                                {
                                    31 => 0,
                                    28 => 31,
                                    27 => 365 + 33,
                                    10 => 365 + 33 + 27
                                };

                                foreach (var val in data)
                                {
                                    var expectedOrderDate = new DateTimeOffset(DateTime.SpecifyKind(baseline.AddDays(count++), DateTimeKind.Utc));
                                    var expected = expectedOrderDate.AddDays(7);
                                    Assert.Equal(expected, val);
                                }

                            }

                        }

                    }
                }

            }
            finally
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles())
                {
                    file.Delete();
                }

                di.Delete();
            }
        }

        [Fact]
        public async Task CanHandleLazyNumbersWithTypeChanges()
        {
            var path = GetTempPath("Orders");
            try
            {
                using (var store = GetDocumentStore())
                {
                    using (var session = store.OpenAsyncSession())
                    {
                        await session.StoreAsync(new User
                        {
                            // after running the script, this is recognized as Int64
                            Double = 1.0
                        });

                        await session.StoreAsync(new User
                        {
                            // recognized as Decimal
                            Double = 2.22
                        });

                        await session.StoreAsync(new User
                        {
                            // recognized as Double
                            Double = double.MaxValue
                        });

                        await session.StoreAsync(new User
                        {
                            // recognized as Decimal
                            Double = 1.012345
                        });

                        await session.StoreAsync(new User
                        {
                            // recognized as Int64
                            Double = 6
                        });

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
    loadToUsers(noPartition(), {
        double : this.Double
    });";

                    var connectionStringName = $"{store.Database} to local";
                    var configuration = new OlapEtlConfiguration
                    {
                        Name = "olap-test",
                        ConnectionStringName = connectionStringName,
                        RunFrequency = DefaultFrequency,
                        Transforms =
                        {
                            new Transformation
                            {
                                Name = "MonthlySales",
                                Collections = new List<string> {"Users"},
                                Script = script
                            }
                        }
                    };

                    SetupLocalOlapEtl(store, configuration, path, connectionStringName);


                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    var files = Directory.GetFiles(path);
                    Assert.Equal(1, files.Length);

                    var expectedFields = new[] { "double", ParquetTransformedItems.DefaultIdColumn, ParquetTransformedItems.LastModifiedColumn };

                    using (var fs = File.OpenRead(files[0]))
                    using (var parquetReader = new ParquetReader(fs))
                    {
                        Assert.Equal(1, parquetReader.RowGroupCount);
                        Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                        using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                        foreach (var field in parquetReader.Schema.Fields)
                        {
                            Assert.True(field.Name.In(expectedFields));
                            var data = rowGroupReader.ReadColumn((DataField)field).Data;

                            Assert.True(data.Length == 5);

                            if (field.Name != "double")
                                continue;

                            var count = 0;
                            var expectedValues = new[]
                            {
                                1.0, 2.22, double.MaxValue, 1.012345, 6
                            };

                            foreach (var val in data)
                            {
                                Assert.Equal(expectedValues[count++], val);
                            }
                        }
                    }
                }
            }
            finally
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles())
                {
                    file.Delete();
                }

                di.Delete();
            }
        }

        [Fact]
        public async Task SimpleTransformation_CanUseSampleData()
        {
            var path = GetTempPath("Orders");
            try
            {
                using (var store = GetDocumentStore())
                {
                    await store.Maintenance.SendAsync(new CreateSampleDataOperation());

                    WaitForIndexing(store);

                    long expectedCount;
                    using (var session = store.OpenAsyncSession())
                    {
                        var query = session.Query<Order>()
                            .GroupBy(o => new
                            {
                                o.OrderedAt.Year,
                                o.OrderedAt.Month
                            });

                        expectedCount = await query.CountAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var key = new Date(year, month);

for (var i = 0; i < this.Lines.length; i++) {
    var line = this.Lines[i];
    
    // load to 'sales' table

    loadToSales(partitionBy(key), {
        Qty: line.Quantity,
        Product: line.Product,
        Cost: line.PricePerUnit
    });
}";
                    SetupLocalOlapEtl(store, script, path);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    var files = Directory.GetFiles(path);
                    Assert.Equal(expectedCount, files.Length);
                }
            }
            finally
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles())
                {
                    file.Delete();
                }

                di.Delete();
            }
        }

        [Fact]
        public async Task CanSpecifyColumnTypeInScript()
        {
            var path = GetTempPath("Orders");
            try
            {
                using (var store = GetDocumentStore())
                {
                    using (var session = store.OpenAsyncSession())
                    {
                        var today = DateTime.Today;
                        await session.StoreAsync(new Order
                        {
                            OrderedAt = today,
                            Lines = new List<OrderLine>
                            {
                                new OrderLine
                                {
                                    ProductName = "Cheese",
                                    PricePerUnit = 18,
                                    Quantity = 1
                                },
                                new OrderLine
                                {
                                    ProductName = "Eggs",
                                    PricePerUnit = 12.75M,
                                    Quantity = 2
                                },
                                new OrderLine
                                {
                                    ProductName = "Chicken",
                                    PricePerUnit = 42.99M,
                                    Quantity = 2
                                }
                            }
                        });

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
for (var i = 0; i < this.Lines.length; i++) {
    var line = this.Lines[i];
    
    // load to 'sales' table

    loadToSales(noPartition(), {
        Quantity: line.Quantity,
        Product: line.ProductName,
        Cost: { Value: line.PricePerUnit, Type: 'Double' }
    });
}";
                    SetupLocalOlapEtl(store, script, path);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    var files = Directory.GetFiles(path);
                    Assert.Equal(1, files.Length);

                    var expectedFields = new[] { "Quantity", "Product", "Cost", ParquetTransformedItems.DefaultIdColumn, ParquetTransformedItems.LastModifiedColumn };

                    using (var fs = File.OpenRead(files[0]))
                    using (var parquetReader = new ParquetReader(fs))
                    {
                        Assert.Equal(1, parquetReader.RowGroupCount);
                        Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                        using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                        foreach (var field in parquetReader.Schema.Fields)
                        {
                            Assert.True(field.Name.In(expectedFields));

                            var dataField = (DataField)field;
                            var data = rowGroupReader.ReadColumn(dataField).Data;

                            Assert.True(data.Length == 3);

                            if (field.Name != "Cost")
                                continue;

                            Assert.Equal(DataType.Double, dataField.DataType);

                            var count = 0;
                            var expectedValues = new[]
                            {
                                18, 12.75, 42.99
                            };

                            foreach (var val in data)
                            {
                                Assert.Equal(expectedValues[count++], val);
                            }
                        }
                    }
                }
            }
            finally
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles())
                {
                    file.Delete();
                }

                di.Delete();
            }
        }

        [Fact]
        public async Task CanSpecifyColumnTypeInScript2()
        {
            var path = GetTempPath("Users");
            try
            {
                using (var store = GetDocumentStore())
                {
                    using (var session = store.OpenAsyncSession())
                    {
                        await session.StoreAsync(new User
                        {
                            Byte = 1,
                            Decimal = 2.02M,
                            Double = 3.033,
                            Float = 4.0444F,
                            Int16 = 5,
                            Int32 = 6,
                            Int64 = 7L,
                            SByte = 8,
                            UInt16 = 9,
                            UInt32 = 10,
                            UInt64 = 11L
                        });

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
loadToUsers(noPartition(), {
    Byte: { Value: this.Byte, Type: 'Byte' },
    Decimal: { Value: this.Decimal, Type: 'Decimal' },
    Double: { Value: this.Double, Type: 'Double' },
    Float: { Value: this.Float, Type: 'Single' },
    Int16: { Value: this.Int16, Type: 'Int16' }, 
    Int32: { Value: this.Int32, Type: 'Int32' },
    Int64: { Value: this.Int64, Type: 'Int64' },
    SByte: { Value: this.SByte, Type: 'SByte' },
    UInt16: { Value: this.UInt16, Type: 'UInt16' },
    UInt32: { Value: this.UInt32, Type: 'UInt32' },
    UInt64: { Value: this.UInt64, Type: 'UInt64' }
});
";
                    var connectionStringName = $"{store.Database} to local";
                    var configuration = new OlapEtlConfiguration
                    {
                        Name = "olap-test",
                        ConnectionStringName = connectionStringName,
                        RunFrequency = DefaultFrequency,
                        Transforms =
                        {
                            new Transformation
                            {
                                Name = "UsersData",
                                Collections = new List<string> {"Users"},
                                Script = script
                            }
                        }
                    };

                    SetupLocalOlapEtl(store, configuration, path, connectionStringName);
                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    var files = Directory.GetFiles(path);
                    Assert.Equal(1, files.Length);

                    using (var fs = File.OpenRead(files[0]))
                    using (var parquetReader = new ParquetReader(fs))
                    {
                        Assert.Equal(1, parquetReader.RowGroupCount);
                        Assert.Equal(13, parquetReader.Schema.Fields.Count);

                        using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                        foreach (var field in parquetReader.Schema.Fields)
                        {
                            var dataField = (DataField)field;
                            var data = rowGroupReader.ReadColumn(dataField).Data;
                            Assert.Equal(1, data.Length);

                            object expected = default;
                            switch (field.Name)
                            {
                                case nameof(User.Byte):
                                    Assert.Equal(DataType.Byte, dataField.DataType);
                                    expected = (byte)1;
                                    break;
                                case nameof(User.Decimal):
                                    Assert.Equal(DataType.Decimal, dataField.DataType);
                                    expected = 2.02M;
                                    break;
                                case nameof(User.Double):
                                    Assert.Equal(DataType.Double, dataField.DataType);
                                    expected = 3.033;
                                    break;
                                case nameof(User.Float):
                                    Assert.Equal(DataType.Float, dataField.DataType);
                                    expected = 4.0444F;
                                    break;
                                case nameof(User.Int16):
                                    Assert.Equal(DataType.Short, dataField.DataType);
                                    expected = (short)5;
                                    break;
                                case nameof(User.Int32):
                                    Assert.Equal(DataType.Int32, dataField.DataType);
                                    expected = 6;
                                    break;
                                case nameof(User.Int64):
                                    Assert.Equal(DataType.Int64, dataField.DataType);
                                    expected = 7L;
                                    break;
                                case nameof(User.SByte):
                                    Assert.Equal(DataType.SignedByte, dataField.DataType);
                                    expected = (sbyte)8;
                                    break;
                                case nameof(User.UInt16):
                                    Assert.Equal(DataType.UnsignedInt16, dataField.DataType);
                                    expected = (ushort)9;
                                    break;
                                case nameof(User.UInt32):
                                    Assert.Equal(DataType.UnsignedInt32, dataField.DataType);
                                    expected = (uint)10;
                                    break;
                                case nameof(User.UInt64):
                                    Assert.Equal(DataType.UnsignedInt64, dataField.DataType);
                                    expected = (ulong)11;
                                    break;
                                case ParquetTransformedItems.DefaultIdColumn:
                                case ParquetTransformedItems.LastModifiedColumn:
                                    continue;
                            }

                            foreach (var value in data)
                            {
                                Assert.Equal(expected, value);
                            }
                        }
                    }
                }
            }
            finally
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles())
                {
                    file.Delete();
                }

                di.Delete();
            }
        }

        private static string GenerateConfigurationScript(string path, out string command)
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), Path.ChangeExtension(Guid.NewGuid().ToString(), ".ps1"));
            var localSetting = new LocalSettings
            {
                FolderPath = path
            };

            var localSettingsString = JsonConvert.SerializeObject(localSetting);

            string script;

            if (PlatformDetails.RunningOnPosix)
            {
                command = "bash";
                script = $"#!/bin/bash\r\necho '{localSettingsString}'";
                File.WriteAllText(scriptPath, script);
                Process.Start("chmod", $"700 {scriptPath}");
            }
            else
            {
                command = "powershell";
                script = $"echo '{localSettingsString}'";
                File.WriteAllText(scriptPath, script);
            }

            return scriptPath;
        }

        private static string GetTempPath(string collection, [CallerMemberName] string caller = null)
        {
            var tmpPath = Path.GetTempPath();
            return Directory.CreateDirectory(Path.Combine(tmpPath, caller, collection)).FullName;
        }

        private void SetupLocalOlapEtl(DocumentStore store, string script, string path, string name = "olap-test", string frequency = null, string transformationName = null)
        {
            var connectionStringName = $"{store.Database} to local";
            var configuration = new OlapEtlConfiguration
            {
                Name = name,
                ConnectionStringName = connectionStringName,
                RunFrequency = frequency ?? DefaultFrequency,
                Transforms =
                {
                    new Transformation
                    {
                        Name = transformationName ?? "MonthlyOrders",
                        Collections = new List<string> {"Orders"},
                        Script = script
                    }
                }
            };

            SetupLocalOlapEtl(store, configuration, path, connectionStringName);
        }

        private void SetupLocalOlapEtl(DocumentStore store, OlapEtlConfiguration configuration, string path, string connectionStringName)
        {
            var connectionString = new OlapConnectionString
            {
                Name = connectionStringName,
                LocalSettings = new LocalSettings
                {
                    FolderPath = path
                }
            };

            AddEtl(store, configuration, connectionString);
        }

        private class User
        {
            public decimal Decimal { get; set; }

            public long Int64 { get; set; }

            public double Double { get; set; }

            public byte Byte { get; set; }

            public sbyte SByte { get; set; }

            public float Float { get; set; }

            public short Int16 { get; set; }

            public int Int32 { get; set; }

            public ushort UInt16 { get; set; }

            public uint UInt32 { get; set; }

            public ulong UInt64 { get; set; }
        }
    }
}
