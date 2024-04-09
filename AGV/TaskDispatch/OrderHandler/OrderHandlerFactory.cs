using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.AGV.TaskDispatch.Exceptions;
using VMSystem.VMS;

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
            { ACTION_TYPE.Measure,  new ExchangeBatteryOrderHandler() },
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
            int transferStationTag = order.OrderData.ChangeAGVMiddleStationTag;

            var nextAGV = VMSManager.GetAGVByName(order.OrderData.TransferToDestineAGVName);

            order.OrderData.From_Station = transferStationTag + "";
            order.OrderData.need_change_agv = false;
            order.OrderData.DesignatedAGVName = order.OrderData.TransferToDestineAGVName;
            var nextOrderHandler = CreateHandler(order.OrderData);
            nextAGV.taskDispatchModule.OrderHandler = nextOrderHandler;
            nextOrderHandler.StartOrder(nextAGV);
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
                    _queue.Enqueue(new NormalMoveTask(_agv, orderData));
                }
                else
                {
                    _queue.Enqueue(new AMCAGVMoveTask(_agv, orderData));
                }
                return _queue;
            }

            if (orderData.Action == ACTION_TYPE.Unload)
            {
                _queue.Enqueue(new MoveToDestineTask(_agv, orderData));
                _queue.Enqueue(new UnloadAtDestineTask(_agv, orderData));
                return _queue;
            }
            if (orderData.Action == ACTION_TYPE.Load)
            {
                _queue.Enqueue(new MoveToDestineTask(_agv, orderData));
                _queue.Enqueue(new LoadAtDestineTask(_agv, orderData));
                return _queue;
            }
            if (orderData.Action == ACTION_TYPE.Charge)
            {
                _queue.Enqueue(new MoveToDestineTask(_agv, orderData));
                _queue.Enqueue(new ChargeTask(_agv, orderData));
                return _queue;
            }
            if (orderData.Action == ACTION_TYPE.ExchangeBattery)
            {
                _queue.Enqueue(new MoveToDestineTask(_agv, orderData));
                _queue.Enqueue(new ExchangeBatteryTask(_agv, orderData));
                return _queue;
            }
            if (orderData.Action == ACTION_TYPE.Park)
            {
                _queue.Enqueue(new MoveToDestineTask(_agv, orderData));
                _queue.Enqueue(new ParkTask(_agv, orderData));
            }

            if (orderData.Action == ACTION_TYPE.Carry)
            {
                _queue.Enqueue(new MoveToSourceTask(_agv, orderData));
                _queue.Enqueue(new UnloadAtSourceTask(_agv, orderData));

                _queue.Enqueue(new MoveToDestineTask(_agv, orderData));
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
                _queue.Enqueue(new MoveToDestineTask(_agv, orderData));
                _queue.Enqueue(new MeasureTask(_agv, orderData));
            }

            return _queue;
        }


        IAGV GetIAGVByName(string agvName)
        {
            return VMSManager.AllAGV.FirstOrDefault(agv => agv.Name == agvName);
        }

        bool IsAGVAtWorkStation(IAGV agv)
        {
            return agv.currentMapPoint.StationType != STATION_TYPE.Normal;
        }
    }
}
