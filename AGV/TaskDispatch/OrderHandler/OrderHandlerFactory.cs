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
                    Dictionary<int, List<int>> transfer_to_from_stations = GetTransferStationTag(orderData).GetAwaiter().GetResult();
                    LoadAtTransferStationTask task = new LoadAtTransferStationTask(_agv, orderData);
                    task.dict_Transfer_to_from_tags = transfer_to_from_stations;
                    //orderData.TransferToTag = _transferTo;
                    //orderData.TransferFromTag = _transferFrom.FirstOrDefault(); // TODO 可能有多個轉運站需再調整
                    _queue.Enqueue(task);
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
                    Dictionary<int, List<int>> transfer_to_from_stations = GetTransferStationTag(orderData).GetAwaiter().GetResult();
                    LoadAtTransferStationTask task = new LoadAtTransferStationTask(_agv, orderData);
                    task.dict_Transfer_to_from_tags = transfer_to_from_stations;
                    //orderData.TransferToTag = _transferTo;
                    //orderData.TransferFromTag = _transferFrom.FirstOrDefault(); // TODO 可能有多個轉運站需再調整
                    _queue.Enqueue(task);
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

        public static async Task<Dictionary<int, List<int>>> GetTransferStationTag(clsTaskDto orderData)
        {
            Dictionary<int, List<int>> dict = new Dictionary<int, List<int>>(); // key: to station, value: from stations
            List<int> TransferStationTags = new List<int>();
            // 從orderData.To_Station_Tag取得可以去的轉換站Tag
            if (orderData.From_Station_Tag == -1) // 放貨任務
                TransferStationTags = await AGVSSerivces.TRANSFER_TASK.GetEQAcceptTransferTagInfoByTag(orderData.To_Station_Tag);
            else
                TransferStationTags = await AGVSSerivces.TRANSFER_TASK.GetEQAcceptTransferTagInfoByTag(orderData.From_Station_Tag);

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
                //從進入點找到EQ內有哪些Tag跟接受車型
                Dictionary<int, int> AcceptAGVInfoOfEQTags = await AGVSSerivces.TRANSFER_TASK.GetEQAcceptAGVTypeInfo(validStations.Select(pt => pt.TagNumber));//key:tag , value :車款
                IAGV toSourceAGV = VMSManager.GetAGVByName(orderData.DesignatedAGVName);
                int toSourceModel = (int)toSourceAGV.model;
                int _transferToTag = AcceptAGVInfoOfEQTags.FirstOrDefault(kp => kp.Value == toSourceModel || kp.Value == (int)AGV_TYPE.Any).Key;
                AcceptAGVInfoOfEQTags.Remove(_transferToTag);
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
                    int toDestineModel = (int)toDestineAGV.model;
                    list_TransferFromTag.AddRange(AcceptAGVInfoOfEQTags.Where(x => x.Value == toDestineModel).Select(x => x.Key).ToList());
                }
                // 如果平對平只有一張tag
                bool isTwoEntryPoints = TransferToMapPoint.Target.Keys.Count > 1;
                if (isTwoEntryPoints)
                    list_TransferFromTag.Add(_transferToTag);

                dict.Add(_transferToTag, list_TransferFromTag);
            }


            //MapPoint TransferToMapPoint = StaMap.GetPointByTagNumber(TransferStationTags.FirstOrDefault());
            //var entryPoints = TransferToMapPoint.Target.Keys.Select(index => StaMap.GetPointByIndex(index));
            //var validStations = entryPoints.SelectMany(pt => pt.Target.Keys.Select(index => StaMap.GetPointByIndex(index)));
            ////從進入點找到EQ內有哪些Tag跟接受車型
            //Dictionary<int, int> AcceptAGVInfoOfEQTags = await AGVSSerivces.TRANSFER_TASK.GetEQAcceptAGVTypeInfo(validStations.Select(pt => pt.TagNumber));//key:tag , value :車款
            //IAGV toSourceAGV = VMSManager.GetAGVByName(orderData.DesignatedAGVName);
            //int toSourceModel = (int)toSourceAGV.model;
            //int _transferToTag = AcceptAGVInfoOfEQTags.FirstOrDefault(kp => kp.Value == toSourceModel || kp.Value == (int)AGV_TYPE.Any).Key;

            //// 如果平對平只有一張tag
            //bool isTwoEntryPoints = TransferToMapPoint.Target.Keys.Count > 1;
            //if (isTwoEntryPoints)
            //{
            //    TransferStationTags.Add(_transferToTag);
            //    return (_transferToTag, TransferStationTags);
            //}
            //AcceptAGVInfoOfEQTags.Remove(_transferToTag);
            //var toSourceModelTag = AcceptAGVInfoOfEQTags.Where(x => x.Value == toSourceModel).Select(x => x.Key).ToList();
            //// 移除跟來源車型一樣的Tag剩下的AcceptAGVInfoOfEQTags為跟來源車型不一樣或是Any
            //foreach (var item in toSourceModelTag)
            //{
            //    AcceptAGVInfoOfEQTags.Remove(item);
            //}

            //if (orderData.TransferToDestineAGVName == "")
            //{
            //    TransferStationTags.AddRange(AcceptAGVInfoOfEQTags.Select(x => x.Key).ToList());
            //}
            //else
            //{
            //    IAGV toDestineAGV = VMSManager.GetAGVByName(orderData.TransferToDestineAGVName);
            //    int toDestineModel = (int)toDestineAGV.model;
            //    TransferStationTags.AddRange(AcceptAGVInfoOfEQTags.Where(x => x.Value == toDestineModel).Select(x => x.Key).ToList());
            //}
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
