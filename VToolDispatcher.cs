
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace V_Companion
{
    public class VToolDispatcher
    {
        private readonly VController _controller;

        private readonly Queue<string> _resultQueue =
            new Queue<string>();

        private readonly List<PendingCall> _pending =
            new List<PendingCall>();

        private readonly HashSet<string> _seenCallIds =
            new HashSet<string>();

        private readonly Queue<string> _seenCallOrder =
            new Queue<string>();

        private const float ENTER_TIMEOUT = 45f;
        private const float NAVIGATION_TIMEOUT = 600f;
        private const float OBJECT_APPROACH_TIMEOUT = 50f;

        private class PendingCall
        {
            public string callId;
            public string tool;
            public string expectedValue;
            public float startedAt;
            public float timeout;
        }

        public VToolDispatcher(VController controller)
        {
            _controller = controller;
        }

        // ========================================================
        // ПРИЁМ И ВЫПОЛНЕНИЕ ИНСТРУМЕНТА
        // ========================================================

        public void Dispatch(
            string callId,
            string toolName,
            string seatRole,
            string destination,
            string objectId,
            float radius,
            int limit)
        {
            if (string.IsNullOrEmpty(callId))
            {
                EnqueueResult(
                    "",
                    toolName,
                    "rejected",
                    null,
                    "invalid_call_id",
                    "Отсутствует call_id."
                );
                return;
            }

            // Защита от повторного выполнения одного вызова.
            if (_seenCallIds.Contains(callId))
            {
                EnqueueResult(
                    callId,
                    toolName,
                    "rejected",
                    null,
                    "duplicate_call_id",
                    "Этот вызов уже обрабатывался."
                );
                return;
            }

            RememberCallId(callId);

            if (_controller == null)
            {
                EnqueueResult(
                    callId,
                    toolName,
                    "rejected",
                    null,
                    "controller_unavailable",
                    "VController ещё не создан."
                );
                return;
            }

            if (string.IsNullOrEmpty(toolName))
            {
                EnqueueResult(
                    callId,
                    "",
                    "rejected",
                    null,
                    "empty_tool",
                    "Не указано название инструмента."
                );
                return;
            }

            // Значения по умолчанию.
            if (string.IsNullOrEmpty(seatRole))
                seatRole = "driver";

            if (string.IsNullOrEmpty(destination))
                destination = "";

            if (string.IsNullOrEmpty(objectId))
                objectId = "";

            radius = Mathf.Clamp(radius, 1f, 50f);
            limit = Mathf.Clamp(limit, 1, 100);

            switch (toolName)
            {
                // ================================================
                // НАБЛЮДЕНИЕ
                // ================================================

                case "get_game_state":
                    {
                        EnqueueResult(
                            callId,
                            toolName,
                            "succeeded",
                            BuildGameStateJson(),
                            null,
                            null
                        );
                        break;
                    }

                case "get_nearby_objects":
                    {
                        string result =
                            _controller.GetNearbyObjectsJson(radius, limit);

                        EnqueueResult(
                            callId,
                            toolName,
                            "succeeded",
                            result,
                            null,
                            null
                        );

                        break;
                    }

                case "list_destinations":
                    {
                        List<string> destinations =
                            _controller.GetAllDestinations();

                        string result =
                            "{\"destinations\":" +
                            BuildStringArray(destinations) + "}";

                        EnqueueResult(
                            callId,
                            toolName,
                            "succeeded",
                            result,
                            null,
                            null
                        );
                        break;
                    }

                // ================================================
                // ПЕРЕДВИЖЕНИЕ
                // ================================================

                case "follow_player":
                    {
                        if (_controller.InCar)
                        {
                            Reject(
                                callId, toolName,
                                "v_in_vehicle",
                                "V находится в автомобиле."
                            );
                            break;
                        }

                        _controller.SetFollow(true);

                        if (_controller.IsFollowing)
                            Success(callId, toolName);
                        else
                            Fail(
                                callId, toolName,
                                "follow_not_started",
                                "Не удалось включить следование."
                            );

                        break;
                    }

                case "stop_following":
                    {
                        _controller.SetFollow(false);

                        if (!_controller.IsFollowing)
                            Success(callId, toolName);
                        else
                            Fail(
                                callId, toolName,
                                "follow_not_stopped",
                                "Не удалось остановить следование."
                            );

                        break;
                    }

                case "jump":
                    {
                        if (_controller.InCar)
                        {
                            Reject(
                                callId, toolName,
                                "v_in_vehicle",
                                "Нельзя прыгать, находясь в автомобиле."
                            );
                            break;
                        }

                        _controller.Jump();

                        EnqueueResult(
                            callId,
                            toolName,
                            "succeeded",
                            "{\"command_accepted\":true}",
                            null,
                            null
                        );

                        break;
                    }

                // ================================================
                // ПРЕДМЕТЫ
                // ================================================

                case "approach_object":
                    {
                        if (string.IsNullOrEmpty(objectId))
                        {
                            Reject(
                                callId, toolName,
                                "missing_object_id",
                                "Не передан object_id."
                            );
                            break;
                        }

                        if (_controller.InCar ||
                            _controller.IsEnteringCar)
                        {
                            Reject(
                                callId, toolName,
                                "v_in_vehicle",
                                "V не может подойти к предмету из автомобиля."
                            );
                            break;
                        }

                        if (_controller.HasItem)
                        {
                            Reject(
                                callId, toolName,
                                "hands_not_free",
                                "V уже держит предмет."
                            );
                            break;
                        }

                        bool started =
                            _controller.ApproachObject(objectId);

                        if (!started)
                        {
                            Fail(
                                callId, toolName,
                                "approach_not_started",
                                "Не удалось начать подход к объекту. Возможно, ID устарел или предмет недоступен."
                            );
                            break;
                        }

                        AddPending(
                            callId,
                            toolName,
                            objectId,
                            OBJECT_APPROACH_TIMEOUT
                        );

                        EnqueueResult(
                            callId,
                            toolName,
                            "accepted",
                            "{\"object_id\":" + Quote(objectId) +
                            ",\"message\":\"V начала подход к выбранному объекту.\"}",
                            null,
                            null
                        );

                        break;
                    }

                case "pickup_object":
                    {
                        if (string.IsNullOrEmpty(objectId))
                        {
                            Reject(
                                callId, toolName,
                                "missing_object_id",
                                "Не передан object_id."
                            );
                            break;
                        }

                        if (_controller.HasItem)
                        {
                            Reject(
                                callId, toolName,
                                "hands_not_free",
                                "V уже держит предмет."
                            );
                            break;
                        }

                        bool picked =
                            _controller.PickupObject(objectId);

                        if (picked &&
                            _controller.HasItem &&
                            _controller.HeldObjectId == objectId)
                        {
                            EnqueueResult(
                                callId,
                                toolName,
                                "succeeded",
                                "{\"object_id\":" +
                                Quote(_controller.HeldObjectId) +
                                ",\"object_name\":" +
                                Quote(_controller.HeldObjectName) +
                                "}",
                                null,
                                null
                            );
                        }
                        else
                        {
                            Fail(
                                callId, toolName,
                                "pickup_failed",
                                "Не удалось поднять выбранный объект. Проверь расстояние и доступность предмета."
                            );
                        }

                        break;
                    }

                case "pickup_nearest":
                    {
                        if (_controller.HasItem)
                        {
                            Reject(
                                callId, toolName,
                                "hands_not_free",
                                "V уже держит предмет."
                            );
                            break;
                        }

                        bool picked = _controller.PickupNearby(3.5f);

                        if (picked && _controller.HasItem)
                            Success(callId, toolName);
                        else
                            Fail(
                                callId, toolName,
                                "pickup_failed",
                                "Не удалось поднять предмет."
                            );

                        break;
                    }

                case "drop_item":
                    {
                        if (!_controller.HasItem)
                        {
                            Reject(
                                callId, toolName,
                                "no_item",
                                "V ничего не держит."
                            );
                            break;
                        }

                        _controller.Drop();

                        if (!_controller.HasItem)
                            Success(callId, toolName);
                        else
                            Fail(
                                callId, toolName,
                                "drop_failed",
                                "Не удалось положить предмет."
                            );

                        break;
                    }

                case "throw_item":
                    {
                        if (!_controller.HasItem)
                        {
                            Reject(
                                callId, toolName,
                                "no_item",
                                "V ничего не держит."
                            );
                            break;
                        }

                        _controller.Throw();

                        if (!_controller.HasItem)
                            Success(callId, toolName);
                        else
                            Fail(
                                callId, toolName,
                                "throw_failed",
                                "Не удалось бросить предмет."
                            );

                        break;
                    }

                // ================================================
                // АВТОМОБИЛЬ
                // ================================================

                case "enter_vehicle":
                    {
                        if (_controller.InCar ||
                            _controller.IsEnteringCar)
                        {
                            Reject(
                                callId, toolName,
                                "already_in_vehicle",
                                "V уже находится в машине или выполняет посадку."
                            );
                            break;
                        }

                        if (seatRole != "driver" &&
                            seatRole != "passenger")
                        {
                            Reject(
                                callId, toolName,
                                "invalid_seat_role",
                                "Допустимые места: driver, passenger."
                            );
                            break;
                        }

                        bool started = seatRole == "passenger"
                            ? _controller.EnterCarAsPassenger()
                            : _controller.EnterCar();

                        if (!started)
                        {
                            Fail(
                                callId, toolName,
                                "entry_not_started",
                                "Не удалось начать посадку. Возможно, рядом нет машины."
                            );
                            break;
                        }

                        AddPending(
                            callId,
                            toolName,
                            seatRole,
                            ENTER_TIMEOUT
                        );

                        EnqueueResult(
                            callId,
                            toolName,
                            "accepted",
                            "{\"message\":\"Процесс посадки начат.\"}",
                            null,
                            null
                        );

                        break;
                    }

                case "exit_vehicle":
                    {
                        if (!_controller.InCar)
                        {
                            Reject(
                                callId, toolName,
                                "not_in_vehicle",
                                "V не находится в автомобиле."
                            );
                            break;
                        }

                        _controller.ExitCar();

                        if (!_controller.InCar)
                            Success(callId, toolName);
                        else
                            Fail(
                                callId, toolName,
                                "exit_failed",
                                "Не удалось выйти из автомобиля."
                            );

                        break;
                    }

                case "navigate_to":
                    {
                        if (!_controller.InCar ||
                            _controller.SeatRole != "driver")
                        {
                            Reject(
                                callId, toolName,
                                "not_driver",
                                "V должна находиться за рулём."
                            );
                            break;
                        }

                        if (_controller.IsNavigationActive)
                        {
                            Reject(
                                callId, toolName,
                                "navigation_already_active",
                                "Навигация уже выполняется."
                            );
                            break;
                        }

                        if (string.IsNullOrEmpty(destination))
                        {
                            Reject(
                                callId, toolName,
                                "missing_destination",
                                "Не указано место назначения."
                            );
                            break;
                        }

                        bool started =
                            _controller.StartNavigationTo(destination);

                        if (!started)
                        {
                            Fail(
                                callId, toolName,
                                "navigation_not_started",
                                "Не удалось запустить навигацию к " + destination
                            );
                            break;
                        }

                        AddPending(
                            callId,
                            toolName,
                            destination,
                            NAVIGATION_TIMEOUT
                        );

                        EnqueueResult(
                            callId,
                            toolName,
                            "accepted",
                            "{\"destination\":" +
                            Quote(destination) +
                            ",\"message\":\"Навигация началась.\"}",
                            null,
                            null
                        );

                        break;
                    }

                case "cancel_navigation":
                    {
                        if (!_controller.IsNavigationActive)
                        {
                            Reject(
                                callId, toolName,
                                "navigation_not_active",
                                "Нет активной навигации."
                            );
                            break;
                        }

                        if (_controller.CancelNavigation())
                            Success(callId, toolName);
                        else
                            Fail(
                                callId, toolName,
                                "cancel_failed",
                                "Не удалось отменить навигацию."
                            );

                        break;
                    }

                // ================================================
                // ИГРОК
                // ================================================

                case "unseat_player":
                    {
                        if (!_controller.PlayerIsPassenger)
                        {
                            Reject(
                                callId, toolName,
                                "player_not_passenger",
                                "Игрок не находится на пассажирском месте."
                            );
                            break;
                        }

                        _controller.ExitPlayerFromPassenger();

                        if (!_controller.PlayerIsPassenger)
                            Success(callId, toolName);
                        else
                            Fail(
                                callId, toolName,
                                "unseat_failed",
                                "Не удалось высадить игрока."
                            );

                        break;
                    }

                // ================================================
                // ДВЕРИ
                // ================================================

                case "open_nearest_door":
                    {
                        string message =
                            _controller.InteractWithNearestDoor("OPEN", 5f);

                        HandleDoorResult(
                            callId,
                            toolName,
                            message
                        );

                        break;
                    }

                case "close_nearest_door":
                    {
                        string message =
                            _controller.InteractWithNearestDoor("CLOSE", 5f);

                        HandleDoorResult(
                            callId,
                            toolName,
                            message
                        );

                        break;
                    }

                default:
                    {
                        EnqueueResult(
                            callId,
                            toolName,
                            "rejected",
                            null,
                            "unknown_tool",
                            "Неизвестный инструмент: " + toolName
                        );

                        break;
                    }
            }
        }

        // ========================================================
        // ОБНОВЛЕНИЕ ДЛИТЕЛЬНЫХ ДЕЙСТВИЙ
        // Вызывать каждый кадр из Mod_Update().
        // ========================================================

        public void Tick()
        {
            if (_controller == null)
                return;

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingCall pending = _pending[i];

                float elapsed = Time.time - pending.startedAt;

                // --------------------------------------------
                // ПОСАДКА В МАШИНУ
                // --------------------------------------------

                if (pending.tool == "enter_vehicle")
                {
                    if (_controller.InCar &&
                        !_controller.IsEnteringCar)
                    {
                        if (_controller.SeatRole ==
                            pending.expectedValue)
                        {
                            EnqueueResult(
                                pending.callId,
                                pending.tool,
                                "succeeded",
                                "{\"seat_role\":" +
                                Quote(_controller.SeatRole) + "}",
                                null,
                                null
                            );
                        }
                        else
                        {
                            EnqueueResult(
                                pending.callId,
                                pending.tool,
                                "failed",
                                null,
                                "wrong_seat_role",
                                "V села не на ожидаемое место."
                            );
                        }

                        _pending.RemoveAt(i);
                        continue;
                    }

                    if (elapsed >= 0.5f &&
                        !_controller.IsEnteringCar &&
                        !_controller.InCar)
                    {
                        EnqueueResult(
                            pending.callId,
                            pending.tool,
                            "failed",
                            null,
                            "entry_failed",
                            "Посадка завершилась, но V не оказалась в машине."
                        );

                        _pending.RemoveAt(i);
                        continue;
                    }

                    if (elapsed >= pending.timeout)
                    {
                        EnqueueResult(
                            pending.callId,
                            pending.tool,
                            "timeout",
                            null,
                            "entry_timeout",
                            "Посадка не завершилась за отведённое время."
                        );

                        _pending.RemoveAt(i);
                        continue;
                    }
                }

                // --------------------------------------------
                // НАВИГАЦИЯ
                // --------------------------------------------

                else if (pending.tool == "navigate_to")
                {
                    string outcome = _controller.NavigationOutcome;

                    if (outcome == "succeeded")
                    {
                        EnqueueResult(
                            pending.callId,
                            pending.tool,
                            "succeeded",
                            "{\"destination\":" +
                            Quote(pending.expectedValue) + "}",
                            null,
                            null
                        );

                        _pending.RemoveAt(i);
                        continue;
                    }

                    if (outcome == "failed")
                    {
                        EnqueueResult(
                            pending.callId,
                            pending.tool,
                            "failed",
                            null,
                            "navigation_failed",
                            "Навигация завершилась ошибкой."
                        );

                        _pending.RemoveAt(i);
                        continue;
                    }

                    if (outcome == "cancelled")
                    {
                        EnqueueResult(
                            pending.callId,
                            pending.tool,
                            "failed",
                            null,
                            "navigation_cancelled",
                            "Навигация была отменена."
                        );

                        _pending.RemoveAt(i);
                        continue;
                    }

                    // Резервная проверка завершения безопасной остановки.
                    // Контроллер уже отпустил маршрут и закончил торможение,
                    // но NavigationOutcome всё ещё может быть "stopping".
                    if (
                        outcome == "stopping" &&
                        !_controller.IsPlayingRoute &&
                        !_controller.IsRoutePreparing &&
                        !_controller.IsFinishingRoute
                    )
                    {
                        Debug.Log(
                            "[V][NAV] Безопасная остановка завершена. " +
                            "Закрываю исходный navigate_to."
                        );

                        EnqueueResult(
                            pending.callId,
                            pending.tool,
                            "failed",
                            null,
                            "navigation_cancelled",
                            "Навигация отменена после безопасной остановки."
                        );

                        _pending.RemoveAt(i);
                        continue;
                    }

                    if (elapsed >= pending.timeout)
                    {
                        _controller.CancelNavigation();

                        EnqueueResult(
                            pending.callId,
                            pending.tool,
                            "timeout",
                            null,
                            "navigation_timeout",
                            "Время ожидания навигации истекло."
                        );

                        _pending.RemoveAt(i);
                        continue;
                    }
                }
                // --------------------------------------------
                // ПОДХОД К КОНКРЕТНОМУ ОБЪЕКТУ
                // --------------------------------------------

                else if (pending.tool == "approach_object")
                {
                    string outcome =
                        _controller.ObjectApproachOutcome;

                    string currentObjectId =
                        _controller.ObjectApproachId;

                    if (currentObjectId != pending.expectedValue)
                    {
                        EnqueueResult(
                            pending.callId,
                            pending.tool,
                            "failed",
                            null,
                            "approach_target_mismatch",
                            "Текущая цель подхода не совпадает с выбранным object_id."
                        );

                        _pending.RemoveAt(i);
                        continue;
                    }

                    if (outcome == "succeeded")
                    {
                        EnqueueResult(
                            pending.callId,
                            pending.tool,
                            "succeeded",
                            "{\"object_id\":" +
                            Quote(pending.expectedValue) + "}",
                            null,
                            null
                        );

                        _pending.RemoveAt(i);
                        continue;
                    }

                    if (outcome == "failed" ||
                        outcome == "cancelled")
                    {
                        EnqueueResult(
                            pending.callId,
                            pending.tool,
                            "failed",
                            null,
                            "approach_" + outcome,
                            "V не смогла завершить подход к выбранному объекту."
                        );

                        _pending.RemoveAt(i);
                        continue;
                    }

                    if (outcome == "timeout")
                    {
                        EnqueueResult(
                            pending.callId,
                            pending.tool,
                            "timeout",
                            null,
                            "approach_timeout",
                            "V не успела подойти к выбранному объекту."
                        );

                        _pending.RemoveAt(i);
                        continue;
                    }

                    if (elapsed >= pending.timeout)
                    {
                        _controller.CancelApproachObject();

                        EnqueueResult(
                            pending.callId,
                            pending.tool,
                            "timeout",
                            null,
                            "approach_timeout",
                            "Истекло время ожидания подхода к объекту."
                        );

                        _pending.RemoveAt(i);
                        continue;
                    }
                }
            }
        }

        // ========================================================
        // ПОЛУЧЕНИЕ НАКОПЛЕННЫХ РЕЗУЛЬТАТОВ
        // ========================================================

        public List<string> DrainResults()
        {
            List<string> results = new List<string>();

            while (_resultQueue.Count > 0)
                results.Add(_resultQueue.Dequeue());

            return results;
        }

        private void AddPending(
            string callId,
            string tool,
            string expected,
            float timeout)
        {
            _pending.Add(new PendingCall
            {
                callId = callId,
                tool = tool,
                expectedValue = expected,
                startedAt = Time.time,
                timeout = timeout
            });
        }

        // ========================================================
        // ОБРАБОТКА ДВЕРЕЙ
        // ========================================================

        private void HandleDoorResult(
            string callId,
            string tool,
            string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                Fail(
                    callId, tool,
                    "door_action_failed",
                    "Пустой ответ от дверного механизма."
                );
                return;
            }

            if (message.StartsWith("OK:", StringComparison.Ordinal))
            {
                // Это подтверждает запуск действия,
                // но ещё не фактическое открытие двери.
                EnqueueResult(
                    callId,
                    tool,
                    "succeeded",
                    "{\"command_started\":true,\"physical_state_verified\":false,\"message\":"
                        + Quote(message) + "}",
                    null,
                    null
                );
            }
            else
            {
                Fail(
                    callId, tool,
                    "door_not_found_or_failed",
                    message
                );
            }
        }

        // ========================================================
        // СОСТОЯНИЕ ИГРЫ
        // ========================================================

        private string BuildGameStateJson()
        {
            Vector3 p = _controller.VehiclePosition;

            GameObject playerObject = GameObject.Find("PLAYER");

            Vector3 playerPos = playerObject != null
                ? playerObject.transform.position
                : Vector3.zero;

            Vector3 vPos = _controller.transform.position;
            Vector3 releasedPos = _controller.LastReleasedObjectPosition;

            float playerDistance = playerObject != null
                ? Vector3.Distance(vPos, playerPos)
                : -1f;

            float releasedDistance =
                playerObject != null &&
                !string.IsNullOrEmpty(_controller.LastReleasedObjectId)
                    ? Vector3.Distance(playerPos, releasedPos)
                    : -1f;

            StringBuilder sb = new StringBuilder();

            sb.Append("{");

            sb.Append("\"v_in_car\":");
            sb.Append(Bool(_controller.InCar));
            sb.Append(",");

            sb.Append("\"player_is_passenger\":");
            sb.Append(Bool(_controller.PlayerIsPassenger));
            sb.Append(",");

            sb.Append("\"seat_role\":");
            sb.Append(Quote(_controller.SeatRole));
            sb.Append(",");

            sb.Append("\"is_entering_car\":");
            sb.Append(Bool(_controller.IsEnteringCar));
            sb.Append(",");

            sb.Append("\"v_following\":");
            sb.Append(Bool(_controller.IsFollowing));
            sb.Append(",");

            sb.Append("\"has_item\":");
            sb.Append(Bool(_controller.HasItem));
            sb.Append(",");

            sb.Append("\"held_object_id\":");
            sb.Append(Quote(_controller.HeldObjectId));
            sb.Append(",");

            sb.Append("\"held_object_name\":");
            sb.Append(Quote(_controller.HeldObjectName));
            sb.Append(",");

            sb.Append("\"object_approach_outcome\":");
            sb.Append(Quote(_controller.ObjectApproachOutcome));
            sb.Append(",");

            sb.Append("\"object_approach_id\":");
            sb.Append(Quote(_controller.ObjectApproachId));
            sb.Append(",");

            sb.Append("\"player_distance_m\":");
            sb.Append(F(playerDistance));
            sb.Append(",");

            sb.Append("\"last_released_object_id\":");
            sb.Append(Quote(_controller.LastReleasedObjectId));
            sb.Append(",");

            sb.Append("\"last_released_object_distance_m\":");
            sb.Append(F(releasedDistance));
            sb.Append(",");

            sb.Append("\"last_released_object_position\":[");
            sb.Append(F(releasedPos.x));
            sb.Append(",");
            sb.Append(F(releasedPos.y));
            sb.Append(",");
            sb.Append(F(releasedPos.z));
            sb.Append("],");

            sb.Append("\"route_active\":");
            sb.Append(Bool(_controller.IsPlayingRoute));
            sb.Append(",");

            sb.Append("\"route_preparing\":");
            sb.Append(Bool(_controller.IsRoutePreparing));
            sb.Append(",");

            sb.Append("\"route_finishing\":");
            sb.Append(Bool(_controller.IsFinishingRoute));
            sb.Append(",");

            sb.Append("\"navigation_outcome\":");
            sb.Append(Quote(_controller.NavigationOutcome));
            sb.Append(",");

            sb.Append("\"navigation_destination\":");
            sb.Append(Quote(_controller.NavigationDestination));
            sb.Append(",");

            sb.Append("\"current_route_name\":");
            sb.Append(Quote(_controller.CurrentRouteName));
            sb.Append(",");

            sb.Append("\"route_point_index\":");
            sb.Append(_controller.RoutePointIndex);
            sb.Append(",");

            sb.Append("\"route_point_count\":");
            sb.Append(_controller.RoutePointCount);
            sb.Append(",");

            sb.Append("\"nearest_route_point_distance_m\":");
            sb.Append(F(_controller.NearestRoutePointDistanceMeters));
            sb.Append(",");

            sb.Append("\"vehicle\":{");

            sb.Append("\"available\":");
            sb.Append(Bool(_controller.HasCurrentVehicle));
            sb.Append(",");

            sb.Append("\"position\":[");
            sb.Append(F(p.x));
            sb.Append(",");
            sb.Append(F(p.y));
            sb.Append(",");
            sb.Append(F(p.z));
            sb.Append("],");

            sb.Append("\"speed_kmh\":");
            sb.Append(F(_controller.VehicleSpeedKmh));
            sb.Append(",");

            sb.Append("\"vertical_speed_mps\":");
            sb.Append(F(_controller.VehicleVerticalSpeedMps));
            sb.Append(",");

            sb.Append("\"up_dot\":");
            sb.Append(F(_controller.VehicleUpDot));
            sb.Append(",");

            sb.Append("\"tilt_degrees\":");
            sb.Append(F(_controller.VehicleTiltDegrees));

            sb.Append("}");

            sb.Append("}");

            return sb.ToString();
        }

        // ========================================================
        // ЗАЩИТА ОТ ПОВТОРНЫХ CALL ID
        // ========================================================

        private void RememberCallId(string callId)
        {
            _seenCallIds.Add(callId);
            _seenCallOrder.Enqueue(callId);

            // Не храним бесконечную историю.
            while (_seenCallOrder.Count > 256)
            {
                string oldId = _seenCallOrder.Dequeue();
                _seenCallIds.Remove(oldId);
            }
        }

        // ========================================================
        // ЕДИНЫЙ ФОРМАТ РЕЗУЛЬТАТА
        // ========================================================

        private void Success(string callId, string tool)
        {
            EnqueueResult(
                callId, tool, "succeeded",
                "{}", null, null
            );
        }

        private void Reject(
            string callId,
            string tool,
            string code,
            string message)
        {
            EnqueueResult(
                callId, tool, "rejected",
                null, code, message
            );
        }

        private void Fail(
            string callId,
            string tool,
            string code,
            string message)
        {
            EnqueueResult(
                callId, tool, "failed",
                null, code, message
            );
        }

        private void EnqueueResult(
            string callId,
            string tool,
            string status,
            string resultJson,
            string reasonCode,
            string reasonMessage)
        {
            StringBuilder sb = new StringBuilder();

            sb.Append("{");

            sb.Append("\"call_id\":");
            sb.Append(Quote(callId));
            sb.Append(",");

            sb.Append("\"tool\":");
            sb.Append(Quote(tool));
            sb.Append(",");

            sb.Append("\"status\":");
            sb.Append(Quote(status));
            sb.Append(",");

            sb.Append("\"result\":");
            sb.Append(
                string.IsNullOrEmpty(resultJson)
                    ? "{}"
                    : resultJson
            );
            sb.Append(",");

            sb.Append("\"reason\":");

            if (string.IsNullOrEmpty(reasonCode))
            {
                sb.Append("null");
            }
            else
            {
                sb.Append("{");
                sb.Append("\"code\":");
                sb.Append(Quote(reasonCode));
                sb.Append(",");
                sb.Append("\"message\":");
                sb.Append(Quote(reasonMessage));
                sb.Append("}");
            }

            sb.Append("}");

            _resultQueue.Enqueue(sb.ToString());
        }

        // ========================================================
        // JSON HELPERS
        // ========================================================

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string F(float value)
        {
            return value.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
        }

        private static string Quote(string value)
        {
            if (value == null)
                return "null";

            StringBuilder sb = new StringBuilder("\"");

            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;

                    default:
                        if (c < 32)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }

            sb.Append("\"");
            return sb.ToString();
        }

        private static string BuildStringArray(List<string> values)
        {
            if (values == null || values.Count == 0)
                return "[]";

            StringBuilder sb = new StringBuilder("[");

            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                    sb.Append(",");

                sb.Append(Quote(values[i]));
            }

            sb.Append("]");

            return sb.ToString();
        }
    }
}
