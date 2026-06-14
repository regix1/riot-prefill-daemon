//namespace BattleNetPrefill.Integration.Test
//{
//    [TestFixture]
//    public class LogFileUpToDateTests
//    {
//        [Test]
//        [TestCase("s1")]
//        [Parallelizable(ParallelScope.All)]
//        [ExcludeFromCodeCoverage, Category("SkipCI")]
//        public void LogFilesAreUpToDate(string productCode)
//        {
//            var targetProduct = TactProduct.Parse(productCode);

//            VersionsEntry latestVersion = GetLatestCdnVersion(targetProduct);
//            var currentLogFileVersion = NginxLogParser.GetLatestLogVersionForProduct(AppConfig.LogFileBasePath, targetProduct);

//            Assert.AreEqual(latestVersion.versionsName, currentLogFileVersion);
//        }

//    }
//}
