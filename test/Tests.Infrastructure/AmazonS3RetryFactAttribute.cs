﻿using System;
using System.Runtime.CompilerServices;
using FastTests;
using Newtonsoft.Json;
using Raven.Client.Documents.Operations.Backups;
using xRetry;

namespace Tests.Infrastructure
{
    public class AmazonS3RetryFactAttribute : RetryFactAttribute
    {
        private const string S3CredentialEnvironmentVariable = "S3_CREDENTIAL";

        private static readonly S3Settings _s3Settings;

        public static S3Settings S3Settings => _s3Settings == null ? null : new S3Settings(_s3Settings);

        private static readonly string ParsingError;

        private static readonly bool EnvVariableMissing;

        static AmazonS3RetryFactAttribute()
        {
            var s3SettingsString = Environment.GetEnvironmentVariable(S3CredentialEnvironmentVariable);
            if (s3SettingsString == null)
            {
                EnvVariableMissing = true;
                return;
            }

            try
            {
                _s3Settings = JsonConvert.DeserializeObject<S3Settings>(s3SettingsString);
            }
            catch (Exception e)
            {
                ParsingError = e.ToString();
            }
        }

        public AmazonS3RetryFactAttribute([CallerMemberName] string memberName = "", int maxRetries = 3, int delayBetweenRetriesMs = 0, params Type[] skipOnExceptions)
            : base(maxRetries, delayBetweenRetriesMs, skipOnExceptions)
        {
            if (RavenTestHelper.IsRunningOnCI)
                return;

            if (EnvVariableMissing)
            {
                Skip = $"Test is missing '{S3CredentialEnvironmentVariable}' environment variable.";
                return;
            }

            if (string.IsNullOrEmpty(ParsingError) == false)
            {
                Skip = $"Failed to parse the Amazon S3 settings, error: {ParsingError}";
                return;
            }

            if (_s3Settings == null)
            {
                Skip = $"S3 {memberName} tests missing Amazon S3 settings.";
                return;
            }
        }
    }
}
