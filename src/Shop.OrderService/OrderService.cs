﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper.Extensions;
using DotNetCore.CAP;
using FluentEmail.Core;
using Microsoft.Extensions.Logging;
using Shop.Common;
using Shop.Common.Order;
using Shop.IGoods;
using Shop.IIdentity;
using Shop.IOrder;

namespace Shop.OrderService
{
    /// <summary>
    /// order service implementation
    /// </summary>
    public class OrderService : IOrderService, ICapSubscribe
    {
        private IDapper Dapper { get; }
        private ILogger Logger { get; }
        private IGoodsService GoodsService { get; }
        private ICapPublisher CapBus { get; }
        private IFluentEmail Email { get; }
        private IIdentityService IdentityService { get; }
        public OrderService(IDapper dapper, ILogger<OrderService> logger, IGoodsService goodsService,
            ICapPublisher capBus,IFluentEmail email, IIdentityService identityService)
        {
            Dapper = dapper;
            Logger = logger;
            GoodsService = goodsService;
            CapBus = capBus;
            Email = email;
            IdentityService = identityService;
        }

        /// <summary>
        /// submmit order
        /// </summary>
        /// <param name="order">order info</param>
        /// <returns></returns>
        public async Task<ResponseResult<NewOrderResult>> Submmit(NewOrderAdd order)
        {
            var orderCode = Guid.NewGuid().ToString("N");
            var dateNow = DateTime.Now;
            var strDateNow = dateNow.ToString("yyyy-MM-dd HH:mm:ss");
            var lstGoodsDetail = order.GoodsInfos.Select(i =>
                $"insert into `OrderDetail` (OrderCode,GoodsId,Count,Price,CreatedOn) values('{orderCode}',{i.GoodsId},{i.Count},{i.Price},'{strDateNow}')");

            Dapper.BeginTransaction();
            try
            {
                await Dapper.ExecuteAsync(
                    "insert into `Order` (OrderCode,UserId,PayCode,Amount,PayStatus,OrderStatus,CreatedOn,CompletedTime) values(@OrderCode,@UserId,@PayCode,@Amount,@PayStatus,@OrderStatus,@CreatedOn,@CompletedTime);" +
                    string.Join(";", lstGoodsDetail),
                    new
                    {
                        OrderCode = orderCode,
                        order.UserId,
                        PayCode = string.Empty,
                        Amount = order.GoodsInfos.Sum(i => i.Price * i.Count),
                        PayStatus = (int) PayStatus.UnComplete,
                        OrderStatus = (int) OrderStatus.Submmit,
                        CreatedOn = strDateNow,
                        CompletedTime = new DateTime(1999, 1, 1, 0, 0, 0)
                    });
                //publish message to goods service.
                await CapBus.PublishAsync("route.order.submmit", new OrderPublish
                {
                    UserId = order.UserId,
                    OrderCode = orderCode,
                    GoodsInfos = order.GoodsInfos.Select(i => new GoodsInfoBase {Count = i.Count, GoodsId = i.GoodsId})
                        .ToList()
                }, "callback-stock-update");
                //publish message to send email.
                await CapBus.PublishAsync("route.order.email",
                    new OrderUser { OrderCode = orderCode, UserId = order.UserId, OrderStatus = OrderStatus.Submmit });
                Dapper.CommitTransaction();
                return new ResponseResult<NewOrderResult>
                {
                    Success = true,
                    Result = new NewOrderResult {CreatedOn = dateNow, OrderCode = orderCode},
                    Error = ""
                };
            }
            catch (Exception e)
            {
                //log e.message
                Logger.LogError(e, "submmit order has error");
                Dapper.RollbackTransaction();
                return new ResponseResult<NewOrderResult>
                {
                    Success = false,
                    Result = null,
                    Error = "submmit order has error"
                };
            }
        }

        [CapSubscribe("callback-stock-update")] //correspond the callbackName of publisher
        public async Task AcceptUpdateStockResult(ResponseResult<OrderPublish> result)
        {
            Dapper.BeginTransaction();
            try
            {
                if (!result.Success)
                {
                    var updateResult =
                        await UpdateOrderStatus(result.Result.OrderCode, OrderStatus.Failed, result.Error);
                    if (updateResult.Result)
                    {
                        //send email
                        await CapBus.PublishAsync("route.order.email",
                            new OrderUser
                            {
                                OrderCode = result.Result.OrderCode,
                                UserId = result.Result.UserId,
                                OrderStatus = OrderStatus.Failed
                            });
                        Dapper.CommitTransaction();
                    }
                }
            }
            catch (Exception e)
            {
                //log e.message
                Logger.LogError(e, "accepting update stock result has error");
                Dapper.RollbackTransaction();
            }
        }

        /// <summary>
        /// update order status
        /// </summary>
        /// <param name="orderCode">order uid</param>
        /// <param name="status">order status</param>
        /// <returns></returns>
        public async Task<ResponseResult<bool>> UpdateOrderStatus(string orderCode, OrderStatus status, string orderResult = "")
        {
            try
            {
                var order = await Dapper.QueryFirstOrDefaultAsync<NewOrderBase>(
                    "select OrderCode,OrderStatus,PayStatus from `Order` where OrderCode=@orderCode", new {orderCode});
                if (order.OrderStatus == status)
                {
                    //log
                    Logger.LogError($"order code is ：{orderCode},updated status is the same to the old status.");
                    return new ResponseResult<bool>
                    {
                        Result = false,
                        Error = $"operation not permitted.",
                        Success = false
                    };
                }

                if (order.OrderStatus == OrderStatus.Delete) //deleted order cann't be handle
                {
                    //log
                    Logger.LogError($"order code is ：{orderCode},deleted order cann't be handled.");
                    return new ResponseResult<bool>
                    {
                        Result = false,
                        Error = $"operation not permitted.",
                        Success = false
                    };
                }
                if (order.OrderStatus == OrderStatus.Failed) //failed order can only be delete
                {
                    if (status != OrderStatus.Delete)
                    {
                        //log
                        Logger.LogError($"order code is ：{orderCode},failed order can only be deleted.");
                        return new ResponseResult<bool>
                        {
                            Result = false,
                            Error = $"operation not permitted.",
                            Success = false
                        };
                    }
                }

                if (order.OrderStatus == OrderStatus.Cancel) //cancelled order can only be deleted
                {
                    if (status != OrderStatus.Delete)
                    {
                        //log
                        Logger.LogError($"order code is ：{orderCode},cancelled order can only be deleted.");
                        return new ResponseResult<bool>
                        {
                            Result = false,
                            Error = $"operation not permitted.",
                            Success = false
                        };
                    }
                }

                if (order.OrderStatus == OrderStatus.Submmit) //submmitted order can be cancelled or failed.
                {
                    if (status != OrderStatus.Cancel && status != OrderStatus.Failed)
                    {
                        //log
                        Logger.LogError($"order code is ：{orderCode},submmitted order can only be cancelled or failed.");
                        return new ResponseResult<bool>
                        {
                            Result = false,
                            Error = $"operation not permitted.",
                            Success = false
                        };
                    }
                }

                if (order.OrderStatus == OrderStatus.Complete) //completed order can only be deleted
                {
                    if (status != OrderStatus.Delete)
                    {
                        //log
                        Logger.LogError($"order code is ：{orderCode},completed order can only be deleted.");
                        return new ResponseResult<bool>
                        {
                            Result = false,
                            Error = $"operation not permitted.",
                            Success = false
                        };
                    }
                }

                var result = await Dapper.ExecuteAsync(
                    "update `Order` set OrderStatus=@status,Result=@orderResult where OrderCode=@orderCode",
                    new {status, orderResult, orderCode});
                if (result == 1)
                {
                    return new ResponseResult<bool>
                    {
                        Result = true,
                        Error = "",
                        Success = true
                    };
                }
                else
                {
                    //log
                    Logger.LogError($"order code is ：{orderCode},order status was not changed.");
                    return new ResponseResult<bool>
                    {
                        Result = false,
                        Error = $"operation failed.",
                        Success = false
                    };
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"order code is ：{orderCode},order status changed has error.");
                return new ResponseResult<bool>
                {
                    Result = false,
                    Error = $"operation has error.",
                    Success = false
                };
            }
        }

        /// <summary>
        /// get all orders from user id.
        /// </summary>
        /// <param name="userId">user id</param>
        /// <returns></returns>
        public async Task<List<OrderItemResult>> GetAllOrder(int userId)
        {
            var lstOrder = await Dapper.QueryAsync<OrderItemResult>(
                "select OrderCode,OrderStatus,PayStatus,CreatedOn,Result from `Order` where UserId=@userId order by CreatedOn desc",
                new {userId});
            var lstCode = lstOrder.Select(i => i.OrderCode).ToList();
            var lstOrderDetail = await Dapper.QueryAsync<NewOrderDetail>(
                "select OrderCode,GoodsId,Count,Price from OrderDetail where OrderCode in @lstCode", new {lstCode});
            var lstGoodsId = lstOrderDetail.Select(i => i.GoodsId).ToList();
            var lstGoods = await GoodsService.GoodsInfos(lstGoodsId);
            var result = new List<OrderItemResult>();
            lstOrder.ForEach(i =>
            {
                var order = new OrderItemResult
                {
                    CreatedOn = i.CreatedOn,
                    OrderCode = i.OrderCode,
                    OrderStatus = i.OrderStatus,
                    PayStatus = i.PayStatus,
                    Result = i.Result,
                    GoodsInfos = new List<GoodsInfoObj>(),
                };
                var lstDetail = lstOrderDetail.Where(j => j.OrderCode == i.OrderCode).ToList();
                lstDetail.ForEach(j =>
                {
                    var srcGoods = lstGoods.FirstOrDefault(k => k.Id == j.GoodsId);
                    order.GoodsInfos.Add(new GoodsInfoObj
                    {
                        Count = j.Count,
                        GoodsId = j.GoodsId,
                        Price = j.Price,
                        Pic = srcGoods?.Pic,
                        Title = srcGoods?.Title
                    });
                });
                order.Amount = order.GoodsInfos.Sum(k => k.Count * k.Price);
                result.Add(order);
            });
            return result;
        }

        /// <summary>
        /// get specified order by order code.
        /// </summary>
        /// <param name="orderCode">order id</param>
        /// <returns></returns>
        public async Task<(bool Succeed, OrderItemResult Order, string ErrorMessage)> GetOrder(int userId,
            string orderCode)
        {
            var order = await Dapper.QueryFirstOrDefaultAsync<OrderItemResult>(
                "select OrderCode,OrderStatus,PayStatus,CreatedOn,Result from `Order` where OrderCode=@orderCode and UserId=@userId",
                new {orderCode, userId});
            if (order == null) return (false, null, "order not exists");
            var lstDetail = await Dapper.QueryAsync<NewOrderDetail>(
                "select OrderCode,GoodsId,Count,Price from OrderDetail where OrderCode=@orderCode", new {orderCode});
            var lstGoodsId = lstDetail.Select(i => i.GoodsId).ToList();
            var lstGoods = await GoodsService.GoodsInfos(lstGoodsId);
            order.GoodsInfos = new List<GoodsInfoObj>();
            lstDetail.ForEach(j =>
            {
                var srcGoods = lstGoods.FirstOrDefault(k => k.Id == j.GoodsId);
                order.GoodsInfos.Add(new GoodsInfoObj
                {
                    Count = j.Count,
                    GoodsId = j.GoodsId,
                    Price = j.Price,
                    Pic = srcGoods?.Pic,
                    Title = srcGoods?.Title
                });
            });
            order.Amount = order.GoodsInfos.Sum(k => k.Count * k.Price);
            return (true, order, "");
        }

        /// <summary>
        /// cancel order.
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="orderCode"></param>
        /// <returns></returns>
        public async Task<ResponseResult<bool>> CancelOrder(int userId, string orderCode)
        {
            Dapper.BeginTransaction();
            try
            {
                var updateResult = await UpdateOrderStatus(orderCode, OrderStatus.Cancel, "cancel order.");
                if (updateResult.Result)
                {
                    //send email
                    await CapBus.PublishAsync("route.order.email",
                        new OrderUser
                        {
                            OrderCode = orderCode,
                            UserId = userId,
                            OrderStatus = OrderStatus.Cancel
                        });
                    Dapper.CommitTransaction();
                }

                return updateResult;
            }
            catch (Exception e)
            {
                //log e.message
                Logger.LogError(e, "cancel order has error");
                Dapper.RollbackTransaction();
                return new ResponseResult<bool>
                {
                    Error = "cancel order failed.",
                    Success = false,
                    Result = false
                };
            }
        }
    }
}
