﻿using System.Linq;
using FastTests.Voron;
using Sparrow.Platform;
using Voron;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Voron.Issues
{
    public class RavenDB_10813 : StorageTest
    {
        public RavenDB_10813(ITestOutputHelper output) : base(output)
        {
        }

        private readonly byte[] _masterKey = Sodium.GenerateRandomBuffer((int)Sodium.crypto_aead_xchacha20poly1305_ietf_keybytes());

        protected override void Configure(StorageEnvironmentOptions options)
        {
            base.Configure(options);

            options.Encryption.MasterKey = _masterKey.ToArray();
        }

        [Fact]
        public void Journal_reader_should_skip_encryption_buffers_overwritten_by_later_allocations_on_db_recovery()
        {
            RequireFileBasedPager();

            using (var tx = Env.WriteTransaction())
            {
                tx.LowLevelTransaction.State.NextPageNumber = 10;

                tx.LowLevelTransaction.AllocatePage(1, 3);
                tx.LowLevelTransaction.AllocatePage(1, 4);

                tx.Commit();
            }

            using (var tx = Env.WriteTransaction())
            {
                tx.LowLevelTransaction.FreePage(4);

                tx.LowLevelTransaction.AllocateOverflowRawPage(38888, out _, 3);

                tx.Commit();
            }

            RestartDatabase();

            using (var tx = Env.ReadTransaction())
            {
                // ensure we can decrypt it
                var overflow = tx.LowLevelTransaction.GetPage(3);

                Assert.Equal(3, overflow.PageNumber);
                Assert.Equal(38888, overflow.OverflowSize);
            }
        }
    }
}
