using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.AGV.TaskDispatch.Exceptions;
using VMSystem.VMS;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Microservices.AGVS;
using static AGVSystemCommonNet6.MAP.MapPoint;

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
            hander.OnLoadingAtTransferStationTaskFinish += HandleOnLoadingAtTransferStationTaskFinish;
            hander.OrderData = orderData;
            hander.SequenceTaskQueue = _CreateSequenceTasks(orderData);
            return hander;
        }

        private void HandleOnLoadingAtTransferStationTaskFinish(object? sender, EventArgs e)
        {
            //generate carry task from [transfer station] to [order destine station]
            OrderHandlerBase order = (OrderHandlerBase)sender;
            int transferStationTag = order.OrderData.TransferFromTag;

            var nextAGV = VMSManager.GetAGVByName(order.OrderData.TransferToDestineAGVName);
            order.OrderData.From_Station = transferStationTag + "";
            order.OrderData.need_change_agv = false;
            order.OrderData.DesignatedAGVName = order.OrderData.TransferToDestineAGVName;
            order.OrderData.State = TASK_RUN_STATUS.WAIT;
            //var nextOrderHandler = CreateHandler(order.OrderData);
            //nextAGV.taskDispatchModule.OrderHandler = nextOrderHandler;
            //nextOrderHandler.StartOrder(nextAGV);
            //nextAGV.taskDispatchModule.OrderExecuteState = clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING;
            nextAGV.taskDispatchModule.TryAppendTasksToQueue(new List<clsTaskDto>() { order.OrderData });
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
                _queue.Enqueue(new MoveToDestineTask(_agv, orderData)
                {
                    NextAction = ACTION_TYPE.Load
                });
                _queue.Enqueue(new LoadAtDestineTask(_agv, orderData));
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
                if (orderData.need_change_agv)
                {
                    (int _transferTo, int _transferFrom) = GetTransferStationTag(orderData).GetAwaiter().GetResult();
                    orderData.TransferToTag = _transferTo;
                    orderData.TransferFromTag = _transferFrom;
                }
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
            int tagOfMiddleStation = orderData.ChangeAGVMiddleStationTag;
            MapPoint stationMapPoint = StaMap.GetPointByTagNumber(tagOfMiddleStation);
            var entryPoints = stationMapPoint.Target.Keys.Select(index => StaMap.GetPointByIndex(index));
            var validStations = entryPoints.SelectMany(pt => pt.Target.Keys.Select(index => StaMap.GetPointByIndex(index)));
            Dictionary<int, int> AcceptAGVInfoOfEQTags = await AGVSSerivces.TRANSFER_TASK.GetEQAcceptAGVTypeInfo(validStations.Select(pt => pt.TagNumber));
            IAGV toSourceAGV = VMSManager.GetAGVByName(orderData.DesignatedAGVName);
            IAGV toDestineAGV = VMSManager.GetAGVByName(orderData.TransferToDestineAGVName);
            int toSourceModel = (int)toSourceAGV.model;
            int toDestineModel = (int)toDestineAGV.model;
            int _transferToTag = AcceptAGVInfoOfEQTags.FirstOrDefault(kp => kp.Value == toSourceModel).Key;
            int _transferFromTag = AcceptAGVInfoOfEQTags.FirstOrDefault(kp => kp.Value == toDestineModel).Key;
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
