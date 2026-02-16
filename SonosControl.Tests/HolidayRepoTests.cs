using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Moq.Protected;
using SonosControl.DAL.Repos;
using Xunit;

namespace SonosControl.Tests.Repos
{
    public class HolidayRepoTests
    {
        [Fact]
        public async Task IsHoliday_ReturnsTrue_WhenApiReturnsNonEmptyArray()
        {
            var mockFactory = new Mock<IHttpClientFactory>();
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();

            mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = System.Net.HttpStatusCode.OK,
                    Content = new StringContent("[{\"date\":\"2025-01-01\",\"name\":\"New Year\"}]")
                });

            var client = new HttpClient(mockHttpMessageHandler.Object);
            mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

            var repo = new HolidayRepo(mockFactory.Object);

            var result = await repo.IsHoliday();

            Assert.True(result);
        }

        [Fact]
        public async Task IsHoliday_ReturnsFalse_WhenApiReturnsEmptyArray()
        {
            var mockFactory = new Mock<IHttpClientFactory>();
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();

            mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = System.Net.HttpStatusCode.OK,
                    Content = new StringContent("[]")
                });

            var client = new HttpClient(mockHttpMessageHandler.Object);
            mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

            var repo = new HolidayRepo(mockFactory.Object);

            var result = await repo.IsHoliday();

            Assert.False(result);
        }
    }
}
