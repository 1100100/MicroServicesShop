﻿using System.Threading;
using System.Threading.Tasks;
using Dapper.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Shop.OrderService
{
    public class InitDataService : IHostedService
    {
        private IDapper Dapper { get; }

        private ILogger Logger { get; }

        public InitDataService(IDapper dapper, ILogger<InitDataService> logger)
        {
            Dapper = dapper;
            Logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            //Logger.LogError("Exec order database init service!");
            await Dapper.ExecuteAsync(
                @"
SET sql_mode=(SELECT REPLACE(@@sql_mode,'ONLY_FULL_GROUP_BY',''));
SET FOREIGN_KEY_CHECKS=0;
CREATE TABLE IF NOT EXISTS `Order` (
  `OrderCode` varchar(255) DEFAULT NULL,
  `UserId` int(11) DEFAULT NULL,
  `PayCode` varchar(50) DEFAULT NULL,
  `Amount` decimal(10,2) DEFAULT NULL,
  `PayStatus` tinyint(4) DEFAULT NULL,
  `OrderStatus` tinyint(4) DEFAULT NULL,
  `CreatedOn` datetime DEFAULT NULL,
  `CompletedTime` datetime DEFAULT NULL,
  `Result` varchar(255) DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;
CREATE TABLE IF NOT EXISTS `OrderDetail` (
  `OrderCode` varchar(255) DEFAULT NULL,
  `GoodsId` int(11) DEFAULT NULL,
  `Count` int(11) DEFAULT NULL,
  `Price` decimal(10,2) DEFAULT NULL,
  `CreatedOn` datetime DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;
            ");
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
        }

    }
}
