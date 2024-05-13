using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.AGV.TaskDispatch.Exceptions;
using VMSystem.VMS;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.Microservices.AGVS;
using static AGVSystemCommonNet6.MAP.MapPoint;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class OrderHandlerFactory
    {
        private Dictionary<ACTION_TYPE, OrderHandlerBase> _OrderHandlerMap = new Dictionary<ACTION_TYPE, OrderHandlerBase>() {
            { ACTION_TYPE.None ,  new MoveToOrderHandler() },
            { ACTION_TYPE.Charge ,  new ChargeOrderHandler() },
            { ACTION_TYPE.Load ,  new LoadOrderHandler() },
            { ACTION_TYPE.Unload ,  new UnloadOrderHandler() },
            { ACTION_TYPE.Carry,  new TransferOrderHandler() },
            { ACTION_TYPE.Park,  new ParkOrderHandler() },
            { ACTION_TYPE.ExchangeBattery,  new ExchangeBatteryOrderHandler() },
            { ACTION_TYPE.Measure,  new MeasureOrderHandler() },
        };

        public OrderHandlerFactory() { }
        public OrderHandlerBase CreateHandler(clsTaskDto orderData)
        {
            OrderHandlerBase hander = _OrderHandlerMap[orderData.Action];
            if (orderData.need_change_agv)
                hander.OnLoadingAtTransferStationTaskFinish += HandleOnLoadingAtTransferStationTaskFinish;
            hander.OrderData = orderData;
            hander.SequenceTaskQueue = _CreateSequenceTasks(orderData);
            return hander;
        }

        private void HandleOnLoadingAtTransferStationTaskFinish(object? sender, EventArgs e)
        {
            OrderHandlerBase order = (OrderHandlerBase)sender;
            order.OnLoadingAtTransferStationTaskFinish -= HandleOnLoadingAtTransferStationTaskFinish;

            Task.Factory.StartNew(async () =>
            {
                await Task.Delay(1000);
                //generate carry task from [transfer station] to [order destine station]

                string strPreviousAGV = order.OrderData.DesignatedAGVName;
                var nextAGV = VMSManager.GetAGVByName(order.OrderData.TransferToDestineAGVName);
                order.OrderData.From_Station = order.OrderData.TransferFromTag.ToString();
                order.OrderData.need_change_agv = false;
                order.OrderData.DesignatedAGVName = "";
                order.OrderData.State = TASK_RUN_STATUS.WAIT;
                order.OrderData.need_change_agv = false;
                if (order.OrderData.Action == ACTION_TYPE.Load)
                    order.OrderData.Action = ACTION_TYPE.Carry;

                VMSManager.HandleTaskDBChangeRequestRaising(this, order.OrderData);
                clsTaskDto charge = new clsTaskDto();
                charge.TaskName = $"ACharge_{DateTime.Now.ToString("yyyyMMdd_HHmmssfff")}";
                charge.RecieveTime = DateTime.Now;
                charge.DesignatedAGVName = strPreviousAGV;
                charge.Action = ACTION_TYPE.Charge;
                charge.Carrier_ID = "-1";
                charge.To_Station = "-1";
                charge.State = TASK_RUN_STATUS.WAIT;
                VMSManager.HandleTaskDBChangeRequestRaising(this, charge);
                LOG.INFO($"AUTO Charge task added {charge}");
            });
        }

        private Queue<TaskBase> _CreateSequenceTasks(clsTaskDto orderData)
        {
            IAGV _agv = GetIAGVByName(orderData.DesignatedAGVName);

            if (_agv == null)
                throw new NotFoundAGVException($"{orderData.DesignatedAGVName} not exist at system");

            var _queue = new Queue<TaskBase>();
            MapPoint _agv_current_map_point = _agv.currentMapPoint;
            if (IsAGVAtWorkStation(_agv))
            {
                _queue.Enqueue(new DischargeTask(_agv, orderData));
            }

            if (orderData.Action == ACTION_TYPE.None) //一般走行任務
            {
                if (_agv.model != AGVSystemCommonNet6.clsEnums.AGV_TYPE.INSPECTION_AGV)
                {
                    _queue.Enqueue(new MoveToDestineTask(_agv, orderData));
                }
                else
                {
                    _queue.Enqueue(new AMCAGVMoveTask(_agv, orderData));
                }
                //_queue.Enqueue(new NormalMoveTask(_agv, orderData));

                return _queue;
            }

            if (orderData.Action == ACTION_TYPE.Unload)
            {
                _queue.Enqueue(new MoveToDestineTask(_agv, orderData)
                {
                    NextAction = ACTION_TYPE.Unload
                });
                _queue.Enqueue(new UnloadAtDestineTask(_agv, orderData));
                return _queue;
            }
            if (orderData.Action == ACTION_TYPE.Load) 
            {
                if (orderData.need_change_agv == true)// 如果下放貨任務，但目標EQAGV_TYPE不符則是將貨放到轉運站，此筆任務結束觸發生成Carry任務
                {
                    _queue.Enqueue(new MoveToDestineTask(_agv, orderData)
                    {
                        NextAction = ACTION_TYPE.Load,
                        TransferStage = orderData.need_change_agv ? TransferStage.MoveToTransferStationLoad : TransferStage.NO_Transfer
                    });
                    (int _transferTo, int _transferFrom) = GetTransferStationTag(orderData).GetAwaiter().GetResult();
                    orderData.TransferToTag = _transferTo;
                    orderData.TransferFromTag = _transferFrom;
                    _queue.Enqueue(new LoadAtTransferStationTask(_agv, orderData));
                }
                else
                {
                    _queue.Enqueue(new MoveToDestineTask(_agv, orderData)
                    {
                        NextAction = ACTION_TYPE.Load
                    });
                    _queue.Enqueue(new LoadAtDestineTask(_agv, orderData));
                }
                return _queue;
            }
            if (orderData.Action == ACTION_TYPE.Charge)
            {
                _queue.Enqueue(new MoveToDestineTask(_agv, orderData)
                {
                    NextAction = ACTION_TYPE.Charge
                });
                _queue.Enqueue(new ChargeTask(_agv, orderData));
                return _queue;
            }
            if (orderData.Action == ACTION_TYPE.ExchangeBattery)
            {
                if (_agv.model != AGVSystemCommonNet6.clsEnums.AGV_TYPE.INSPECTION_AGV)
                {
                    _queue.Enqueue(new MoveToDestineTask(_agv, orderData)
                    {
                        NextAction = ACTION_TYPE.ExchangeBattery
                    });
                }
                else
                {
                    _queue.Enqueue(new AMCAGVMoveTask(_agv, orderData)
                    {
                        NextAction = ACTION_TYPE.ExchangeBattery
                    });
                }
                //
                //
                //_queue.Enqueue(new MoveToDestineTask(_agv, orderData));
                _queue.Enqueue(new ExchangeBatteryTask(_agv, orderData));
                return _queue;
            }
            if (orderData.Action == ACTION_TYPE.Park)
            {
                _queue.Enqueue(new MoveToDestineTask(_agv, orderData)
                {
                    NextAction = ACTION_TYPE.Park
                });
                _queue.Enqueue(new ParkTask(_agv, orderData));
            }

            if (orderData.Action == ACTION_TYPE.Carry)
            {
                //if (orderData.need_change_agv)
                //{
                //    (int _transferTo, int _transferFrom) = GetTransferStationTag(orderData).GetAwaiter().GetResult();
                //    orderData.TransferToTag = _transferTo;
                //    orderData.TransferFromTag = _transferFrom;
                //}
                _queue.Enqueue(new MoveToSourceTask(_agv, orderData)
                {
                    NextAction = ACTION_TYPE.Unload
                });
                _queue.Enqueue(new UnloadAtSourceTask(_agv, orderData)
                {
                    NextAction = ACTION_TYPE.None
                });

                _queue.Enqueue(new MoveToDestineTask(_agv, orderData)
                {
                    NextAction = ACTION_TYPE.Load,
                    TransferStage = orderData.need_change_agv ? TransferStage.MoveToTransferStationLoad : TransferStage.NO_Transfer

                });
                if (orderData.need_change_agv)
                {
                    (int _transferTo, int _transferFrom) = GetTransferStationTag(orderData).GetAwaiter().GetResult();
                    orderData.TransferToTag = _transferTo;
                    orderData.TransferFromTag = _transferFrom;
                    _queue.Enqueue(new LoadAtTransferStationTask(_agv, orderData));
                }
                else
                {
                    _queue.Enqueue(new LoadAtDestineTask(_agv, orderData));
                }
                return _queue;
            }
            if (orderData.Action == ACTION_TYPE.Measure)
            {
                if (_agv.model != AGVSystemCommonNet6.clsEnums.AGV_TYPE.INSPECTION_AGV)
                {
                    _queue.Enqueue(new MoveToDestineTask(_agv, orderData)
                    {
                        NextAction = ACTION_TYPE.Measure
                    });
                }
                else
                {
                    _queue.Enqueue(new AMCAGVMoveTask(_agv, orderData)
                    {
                        NextAction = ACTION_TYPE.Measure
                    });
                }
                //_queue.Enqueue(new MoveToDestineTask(_agv, orderData));
                _queue.Enqueue(new MeasureTask(_agv, orderData));
            }

            return _queue;
        }

        public static async Task<(int transferToTag, int transferFromTag)> GetTransferStationTag(clsTaskDto orderData)
        {
            List<int> TransferStationTags = new List<int>();
            // 取得From Station可去的轉換站
            if (orderData.From_Station_Tag == -1)
            {
                TransferStationTags = await AGVSSerivces.TRANSFER_TASK.GetEQAcceptTransferTagInfoByTag(orderData.To_Station_Tag);
            }
            else
            {
                TransferStationTags = await AGVSSerivces.TRANSFER_TASK.GetEQAcceptTransferTagInfoByTag(orderData.From_Station_Tag);
            }
            if (TransferStationTags.Count() <= 0)
                return (-1, -1);

            MapPoint TransferToMapPoint = StaMap.GetPointByTagNumber(TransferStationTags.FirstOrDefault());
            var entryPoints = TransferToMapPoint.Target.Keys.Select(index => StaMap.GetPointByIndex(index));
            var validStations = entryPoints.SelectMany(pt => pt.Target.Keys.Select(index => StaMap.GetPointByIndex(index)));
            Dictionary<int, int> AcceptAGVInfoOfEQTags = await AGVSSerivces.TRANSFER_TASK.GetEQAcceptAGVTypeInfo(validStations.Select(pt => pt.TagNumber));//key:tag , value :車款
            IAGV toSourceAGV = VMSManager.GetAGVByName(orderData.DesignatedAGVName);
            int toSourceModel = (int)toSourceAGV.model;
            int _transferToTag = AcceptAGVInfoOfEQTags.FirstOrDefault(kp => kp.Value == toSourceModel).Key;
            AcceptAGVInfoOfEQTags.Remove(_transferToTag);
            int _transferFromTag = -1;
            if (orderData.TransferToDestineAGVName == "")
            {
                _transferFromTag = AcceptAGVInfoOfEQTags.FirstOrDefault().Key;
            }
            else
            {
                IAGV toDestineAGV = VMSManager.GetAGVByName(orderData.TransferToDestineAGVName);
                int toDestineModel = (int)toDestineAGV.model;
                _transferFromTag = AcceptAGVInfoOfEQTags.FirstOrDefault(kp => kp.Value == toDestineModel).Key;
            }

            // 待確認功能
            //bool isTwoEntryPoints = TransferFromMapPoint.Target.Keys.Count > 1;

            //if (isTwoEntryPoints)
            //    return (orderData.TransferToTag, orderData.TransferFromTag);
            return (_transferToTag, _transferFromTag);
        }

        IAGV GetIAGVByName(string agvName)
        {
            return VMSManager.AllAGV.FirstOrDefault(agv => agv.Name == agvName);
        }

        bool IsAGVAtWorkStation(IAGV agv)
        {
            return agv.currentMapPoint.StationType != STATION_TYPE.Normal && agv.currentMapPoint.StationType != STATION_TYPE.Elevator;
        }
    }
}
