using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.AGV.TaskDispatch.Exceptions;
using VMSystem.VMS;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Microservices.AGVS;
using static AGVSystemCommonNet6.MAP.MapPoint;
using static AGVSystemCommonNet6.clsEnums;
using VMSystem.Dispatch.Equipment;
using static System.Collections.Specialized.BitVector32;
using VMSystem.AGV.TaskDispatch.OrderHandler.OrderTransferSpace;
using VMSystem.TrafficControl;
using VMSystem.AGV.TaskDispatch.OrderHandler.DestineChangeWokers;
using AGVSystemCommonNet6.DATABASE;
using VMSystem.Extensions;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class OrderHandlerFactory
    {
        static AGVSDbContext agvsDb => VMSManager.AGVSDbContext;
        static SemaphoreSlim taskTbModifyLock => VMSManager.tasksLock;

        private Dictionary<ACTION_TYPE, OrderHandlerBase> _OrderHandlerMap = new Dictionary<ACTION_TYPE, OrderHandlerBase>() {
            { ACTION_TYPE.None ,  new MoveToOrderHandler(taskTbModifyLock) },
            { ACTION_TYPE.Charge ,  new ChargeOrderHandler(taskTbModifyLock) },
            { ACTION_TYPE.DeepCharge,  new DeepChargeOrderHandler(taskTbModifyLock) },
            { ACTION_TYPE.Load ,  new LoadOrderHandler(taskTbModifyLock) },
            { ACTION_TYPE.Unload ,  new UnloadOrderHandler(taskTbModifyLock) },
            { ACTION_TYPE.Carry,  new TransferOrderHandler(taskTbModifyLock) },
            { ACTION_TYPE.Park,  new ParkOrderHandler(taskTbModifyLock) },
            { ACTION_TYPE.ExchangeBattery,  new ExchangeBatteryOrderHandler(taskTbModifyLock) },
            { ACTION_TYPE.Measure,  new MeasureOrderHandler(taskTbModifyLock) },
        };

        public OrderHandlerFactory() { }
        public OrderHandlerBase CreateHandler(clsTaskDto orderData)
        {
            OrderHandlerBase hander = _OrderHandlerMap[orderData.Action];
            if (orderData.need_change_agv)
                hander.OnLoadingAtTransferStationTaskFinish += HandleOnLoadingAtTransferStationTaskFinish;
            hander.OrderData = orderData;
            hander.SequenceTaskQueue = _CreateSequenceTasks(orderData);
            hander.BuildTransportCommandDto();
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
                order.OrderData.TaskName += $"_2";
                order.OrderData.From_Station = order.OrderData.TransferFromTag.ToString();
                //order.OrderData.From_Slot = "0";
                //order.OrderData.To_Slot = "-1";
                order.OrderData.need_change_agv = false;
                order.OrderData.DesignatedAGVName = "";
                order.OrderData.State = TASK_RUN_STATUS.WAIT;
                order.OrderData.need_change_agv = false;
                order.OrderData.transfer_task_stage = 2;
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
            });
        }

        private Queue<TaskBase> _CreateSequenceTasks(clsTaskDto orderData)
        {
            IAGV _agv = GetIAGVByName(orderData.DesignatedAGVName);
            if (_agv == null)
                throw new NotFoundAGVException($"{orderData.DesignatedAGVName} not exist at system");

            OrderTransfer OrderTransfer = _IsOrderTransferEnabled() ? CreateOrderTransfer(_agv, orderData) : null;

            if (_agv.IsAGVHasCargoOrHasCargoID() == true)
                orderData.Actual_Carrier_ID = _agv.states.CSTID[0];

            var _queue = new Queue<TaskBase>();
            MapPoint _agv_current_map_point = _agv.currentMapPoint;


            bool _isHotRunOrderSourceAtMainEQ = orderData.TaskName.ToLower().Contains("hr_") && !orderData.From_Station_Tag.IsRackPortStation();
            bool _isHotRunOrderDestineAtMainEQ = orderData.TaskName.ToLower().Contains("hr_") && !orderData.To_Station_Tag.IsRackPortStation();

            if (IsAGVAtWorkStation(_agv))
            {
                _queue.Enqueue(new DischargeTask(_agv, orderData, taskTbModifyLock));
            }

            if (orderData.Action == ACTION_TYPE.None) //一般走行任務
            {
                if (_agv.model != AGVSystemCommonNet6.clsEnums.AGV_TYPE.INSPECTION_AGV)
                {
                    _queue.Enqueue(new MoveToDestineTask(_agv, orderData, taskTbModifyLock));
                }
                else
                {
                    _queue.Enqueue(new AMCAGVMoveTask(_agv, orderData, taskTbModifyLock));
                }
                //_queue.Enqueue(new NormalMoveTask(_agv, orderData));

                return _queue;
            }

            if (orderData.Action == ACTION_TYPE.Unload)
            {
                _queue.Enqueue(new MoveToDestineTask(_agv, orderData, taskTbModifyLock)
                {
                    NextAction = ACTION_TYPE.Unload,
                    OrderTransfer = OrderTransfer
                });
                if (!_isHotRunOrderDestineAtMainEQ)
                    _queue.Enqueue(new UnloadAtDestineTask(_agv, orderData, taskTbModifyLock));
                return _queue;
            }
            if (orderData.Action == ACTION_TYPE.Load)
            {
                if (orderData.need_change_agv == true)// 如果下放貨任務，但目標EQAGV_TYPE不符則是將貨放到轉運站，此筆任務結束觸發生成Carry任務
                {
                    _queue.Enqueue(new MoveToDestineTask(_agv, orderData, taskTbModifyLock)
                    {
                        NextAction = ACTION_TYPE.Load,
                        TransferStage = orderData.need_change_agv ? TransferStage.MoveToTransferStationLoad : TransferStage.NO_Transfer
                    });
                    Dictionary<int, List<int>> transfer_to_from_stations = GetTransferStationTag(orderData);
                    LoadAtTransferStationTask task = new LoadAtTransferStationTask(_agv, orderData, taskTbModifyLock);
                    task.dict_Transfer_to_from_tags = transfer_to_from_stations;

                    if (!_isHotRunOrderDestineAtMainEQ)
                        _queue.Enqueue(task);
                }
                else
                {
                    _queue.Enqueue(new MoveToDestineTask(_agv, orderData, taskTbModifyLock)
                    {
                        NextAction = ACTION_TYPE.Load
                    });
                    if (!_isHotRunOrderDestineAtMainEQ)
                        _queue.Enqueue(new LoadAtDestineTask(_agv, orderData, taskTbModifyLock));
                    else
                        _queue.Enqueue(new VehicleCargoRemoveRequestTask(_agv, orderData, taskTbModifyLock));
                }
                return _queue;
            }
            if (orderData.Action == ACTION_TYPE.Charge || orderData.Action == ACTION_TYPE.DeepCharge)
            {
                _queue.Enqueue(new MoveToDestineTask(_agv, orderData, taskTbModifyLock)
                {
                    NextAction = ACTION_TYPE.Charge,
                    DestineChanger = new ChargeStationChanger(_agv, orderData, taskTbModifyLock)
                });
                _queue.Enqueue(new ChargeTask(_agv, orderData, taskTbModifyLock));
                return _queue;
            }
            if (orderData.Action == ACTION_TYPE.ExchangeBattery)
            {
                if (_agv.model != AGVSystemCommonNet6.clsEnums.AGV_TYPE.INSPECTION_AGV)
                {
                    _queue.Enqueue(new MoveToDestineTask(_agv, orderData, taskTbModifyLock)
                    {
                        NextAction = ACTION_TYPE.ExchangeBattery
                    });
                }
                else
                {
                    _queue.Enqueue(new AMCAGVMoveTask(_agv, orderData, taskTbModifyLock)
                    {
                        NextAction = ACTION_TYPE.ExchangeBattery
                    });
                }
                //
                //
                //_queue.Enqueue(new MoveToDestineTask(_agv, orderData));
                _queue.Enqueue(new ExchangeBatteryTask(_agv, orderData, taskTbModifyLock));
                return _queue;
            }
            if (orderData.Action == ACTION_TYPE.Park)
            {
                _queue.Enqueue(new MoveToDestineTask(_agv, orderData, taskTbModifyLock)
                {
                    NextAction = ACTION_TYPE.Park
                });
                _queue.Enqueue(new ParkTask(_agv, orderData, taskTbModifyLock));
            }

            if (orderData.Action == ACTION_TYPE.Carry)
            {

                if (!orderData.IsFromAGV)
                {
                    _queue.Enqueue(new MoveToSourceTask(_agv, orderData, taskTbModifyLock)
                    {
                        NextAction = ACTION_TYPE.Unload,
                        OrderTransfer = OrderTransfer
                    });
                    if (!_isHotRunOrderSourceAtMainEQ)
                        _queue.Enqueue(new UnloadAtSourceTask(_agv, orderData, taskTbModifyLock)
                        {
                            NextAction = ACTION_TYPE.None
                        });
                }

                _queue.Enqueue(new MoveToDestineTask(_agv, orderData, taskTbModifyLock)
                {
                    NextAction = ACTION_TYPE.Load,
                    TransferStage = orderData.need_change_agv ? TransferStage.MoveToTransferStationLoad : TransferStage.NO_Transfer
                });
                if (orderData.need_change_agv)
                {
                    Dictionary<int, List<int>> transfer_to_from_stations = GetTransferStationTag(orderData);
                    LoadAtTransferStationTask task = new LoadAtTransferStationTask(_agv, orderData, taskTbModifyLock);
                    task.dict_Transfer_to_from_tags = transfer_to_from_stations;
                    if (!_isHotRunOrderDestineAtMainEQ)
                        _queue.Enqueue(task);
                    else
                        _queue.Enqueue(new VehicleCargoRemoveRequestTask(_agv, orderData, taskTbModifyLock));
                }
                else
                {
                    if (!_isHotRunOrderDestineAtMainEQ)
                        _queue.Enqueue(new LoadAtDestineTask(_agv, orderData, taskTbModifyLock));
                    else
                        _queue.Enqueue(new VehicleCargoRemoveRequestTask(_agv, orderData, taskTbModifyLock));
                }
                return _queue;
            }
            if (orderData.Action == ACTION_TYPE.Measure)
            {
                if (_agv.model != AGVSystemCommonNet6.clsEnums.AGV_TYPE.INSPECTION_AGV)
                {
                    _queue.Enqueue(new MoveToDestineTask(_agv, orderData, taskTbModifyLock)
                    {
                        NextAction = ACTION_TYPE.Measure
                    });
                }
                else
                {
                    _queue.Enqueue(new AMCAGVMoveTask(_agv, orderData, taskTbModifyLock)
                    {
                        NextAction = ACTION_TYPE.Measure
                    });
                }
                //_queue.Enqueue(new MoveToDestineTask(_agv, orderData));
                _queue.Enqueue(new MeasureTask(_agv, orderData, taskTbModifyLock));
            }

            return _queue;
        }

        private bool _IsOrderTransferEnabled()
        {
            return SystemModes.RunMode == AGVSystemCommonNet6.AGVDispatch.RunMode.RUN_MODE.RUN && TrafficControlCenter.TrafficControlParameters.Experimental.OrderTransfer.Enabled;
        }
        private OrderTransfer CreateOrderTransfer(IAGV agv, clsTaskDto orderData)
        {
            return new TransferOrderToOtherVehicleMonitor(agv, orderData, TrafficControlCenter.TrafficControlParameters.Experimental.OrderTransfer, VMSManager.tasksLock)
            {
            };
        }

        public static Dictionary<int, List<int>> GetTransferStationTag(clsTaskDto orderData)
        {
            Dictionary<int, List<int>> dict = new Dictionary<int, List<int>>(); // key: to station, value: from stations
            List<int> TransferStationTags = new List<int>();
            // 從orderData.To_Station_Tag取得可以去的轉換站Tag
            if (orderData.From_Station_Tag == -1) // 放貨任務
                TransferStationTags = EquipmentStore.GetTransferTags(orderData.To_Station_Tag);
            else
                TransferStationTags = EquipmentStore.GetTransferTags(orderData.From_Station_Tag);

            if (TransferStationTags.Count() <= 0)
            {
                TransferStationTags.Add(-1);
                return null;
            }

            foreach (var TransferTag in TransferStationTags)
            {
                List<int> list_TransferFromTag = new List<int>();
                MapPoint TransferToMapPoint = StaMap.GetPointByTagNumber(TransferTag);
                var entryPoints = TransferToMapPoint.Target.Keys.Select(index => StaMap.GetPointByIndex(index));
                var validStations = entryPoints.SelectMany(pt => pt.Target.Keys.Select(index => StaMap.GetPointByIndex(index)));
                validStations = validStations.GroupBy(x => x.TagNumber).Select(x => x.First());
                //從進入點找到EQ內有哪些Tag跟接受車型
                Dictionary<int, AGV_TYPE> AcceptAGVInfoOfEQTags = validStations.ToDictionary(station => station.TagNumber, station => EquipmentStore.GetEQAcceptAGVType(station.TagNumber));
                AcceptAGVInfoOfEQTags.Where(x => x.Value != AGV_TYPE.Null).Select(x => x);
                AcceptAGVInfoOfEQTags = AcceptAGVInfoOfEQTags.Where(x => x.Value != AGV_TYPE.Null).Select(x => x).ToDictionary(x => x.Key, x => x.Value);
                if (AcceptAGVInfoOfEQTags.Count <= 0)
                    continue;
                int _transferToTag = -1;
                IAGV toSourceAGV = VMSManager.GetAGVByName(orderData.DesignatedAGVName);
                AGV_TYPE toSourceModel = toSourceAGV.model;
                _transferToTag = AcceptAGVInfoOfEQTags.FirstOrDefault(kp => kp.Value == AGV_TYPE.Any || kp.Value == toSourceModel).Key;
                if (AcceptAGVInfoOfEQTags.Count == 1)    // 如果平對平只有一張tag
                {
                    list_TransferFromTag.Add(_transferToTag);
                }
                else
                {
                    var toSourceModelTag = AcceptAGVInfoOfEQTags.Where(x => x.Value == toSourceModel).Select(x => x.Key).ToList();
                    // 移除跟來源車型一樣的Tag剩下的AcceptAGVInfoOfEQTags為跟來源車型不一樣或是Any
                    foreach (var item in toSourceModelTag)
                    {
                        AcceptAGVInfoOfEQTags.Remove(item);
                    }
                    if (orderData.TransferToDestineAGVName == "")
                    {
                        list_TransferFromTag.AddRange(AcceptAGVInfoOfEQTags.Select(x => x.Key).ToList());
                    }
                    else
                    {
                        IAGV toDestineAGV = VMSManager.GetAGVByName(orderData.TransferToDestineAGVName);
                        AGV_TYPE toDestineModel = toDestineAGV.model;
                        list_TransferFromTag.AddRange(AcceptAGVInfoOfEQTags.Where(x => x.Value == toDestineModel).Select(x => x.Key).ToList());
                    }
                }
                dict.TryAdd(_transferToTag, list_TransferFromTag);
            }
            if (dict.Count <= 0)
                dict = null;
            return dict;
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
