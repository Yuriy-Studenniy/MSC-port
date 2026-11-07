using UnityEngine;

namespace V_Companion
{
	public class VGameObserver
	{
		// Проверяем состояние два раза в секунду.
		private const float SAMPLE_INTERVAL = 0.5f;

		// Скорость ниже этого значения считаем почти полной остановкой.
		private const float MOVING_SPEED_KMH = 2.5f;

		// Дополнительная проверка фактического перемещения.
		private const float MOVEMENT_DISTANCE_M = 0.25f;

		private const float ROLLOVER_ANGLE_DEGREES = 110f;
		private const float ROLLOVER_HOLD_SECONDS = 1.0f;
		private const float RECOVERY_ANGLE_DEGREES = 60f;

		// Включать только для теста, после проверки вернуть false.
		private const bool DEBUG_FORCE_ROLLOVER_EVENT = false;

		// Сколько секунд машина должна не двигаться,
		// прежде чем мы сообщим о возможном застревании.
		private const float STUCK_SECONDS = 10f;

		private float _nextSampleAt = 0f;
		private float _lastMotionAt = -1f;

		private float _rolloverSince = -1f;
		private bool _rolloverReported = false;
		private bool _debugRolloverReported = false;

		private Vector3 _lastPosition;
		private bool _hasLastPosition = false;

		// Не отправляем одно и то же событие каждую секунду.
		private bool _stuckReported = false;

        public bool StuckReported
        {
            get { return _stuckReported || _rolloverReported; }
        }

        public string Tick(VController controller, float now)
{
    if (controller == null ||
        !controller.InCar ||
        !controller.HasCurrentVehicle)
    {
        Reset();
        return null;
    }

    if (now < _nextSampleAt)
        return null;

    _nextSampleAt = now + SAMPLE_INTERVAL;

    float tilt = controller.VehicleTiltDegrees;

    // Искусственное событие для безопасной проверки.
    if (DEBUG_FORCE_ROLLOVER_EVENT && !_debugRolloverReported)
    {
        _debugRolloverReported = true;

        return "ТЕСТ НАБЛЮДАТЕЛЯ: имитация переворота машины. " +
               "Это искусственное событие для проверки реакции.";
    }

    // Настоящий переворот: кузов наклонён более чем на 110 градусов.
    if (tilt >= ROLLOVER_ANGLE_DEGREES)
    {
        if (_rolloverSince < 0f)
            _rolloverSince = now;

        if (!_rolloverReported &&
            now - _rolloverSince >= ROLLOVER_HOLD_SECONDS)
        {
            _rolloverReported = true;

            return "Машина сильно накренилась и, похоже, перевернулась. " +
                   "Угол наклона кузова: " + tilt.ToString("F0") +
                   " градусов. Проанализируй ситуацию и отреагируй.";
        }
    }
    else
    {
        _rolloverSince = -1f;

        // Считаем, что машина восстановила нормальное положение.
        if (_rolloverReported && tilt <= RECOVERY_ANGLE_DEGREES)
            _rolloverReported = false;
    }

    // Ниже оставляем прежнюю проверку застревания на маршруте.
    if (!controller.IsPlayingRoute ||
        controller.IsRoutePreparing ||
        controller.IsFinishingRoute)
    {
        _lastMotionAt = -1f;
        _hasLastPosition = false;
        _stuckReported = false;
        return null;
    }

    Vector3 position = controller.VehiclePosition;
    float speed = controller.VehicleSpeedKmh;

    if (!_hasLastPosition)
    {
        _lastPosition = position;
        _hasLastPosition = true;
        _lastMotionAt = now;
        return null;
    }

    float moved = Vector3.Distance(position, _lastPosition);
    _lastPosition = position;

    if (speed > MOVING_SPEED_KMH || moved >= MOVEMENT_DISTANCE_M)
    {
        _lastMotionAt = now;
        _stuckReported = false;
        return null;
    }

    if (!_stuckReported &&
        now - _lastMotionAt >= STUCK_SECONDS)
    {
        _stuckReported = true;

        string destination = controller.NavigationDestination;
        string route = controller.CurrentRouteName;

        if (string.IsNullOrEmpty(destination))
            destination = "неизвестной цели";

        if (string.IsNullOrEmpty(route))
            route = "неизвестной дороги";

        return "Во время поездки к '" + destination +
            "' машина почти не перемещается уже " +
            STUCK_SECONDS.ToString("F0") +
            " секунд. Текущая дорога: " + route +
            ". Скорость: " + speed.ToString("F1") +
            " км/ч. Расстояние до ближайшей точки маршрута: " +
            controller.NearestRoutePointDistanceMeters.ToString("F1") +
            " м. Возможно, машина застряла или остановилась. " +
            "Проанализируй ситуацию и отреагируй.";
    }

    return null;
}

public void Reset()
{
    _nextSampleAt = 0f;
    _lastMotionAt = -1f;
    _lastPosition = Vector3.zero;
    _hasLastPosition = false;
    _stuckReported = false;

    _rolloverSince = -1f;
    _rolloverReported = false;
    _debugRolloverReported = false;
}
}
}