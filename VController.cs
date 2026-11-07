using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using MSCLoader;

namespace V_Companion
{
    public class VController : MonoBehaviour
    {
        private CharacterController cc;
        private Transform playerTransform;

        public static float WalkSpeed = 1.18f;
        private float gravity = -20f;
        private float verticalVelocity = 0f;
        private bool following = false;
        private float jumpRequestedAt = -10f;

        private Transform grabPoint;
        private GameObject heldObject;
        private Rigidbody heldRb;

        // Внешний импульс, передаваемый V от движущихся физических объектов.
        private Vector3 _externalVelocity = Vector3.zero;
        private CapsuleCollider _impactTrigger;
        private const float EXTERNAL_IMPACT_MASS = 65f;
        private const float EXTERNAL_IMPACT_MAX_SPEED = 6.5f;
        private const float EXTERNAL_IMPACT_DECELERATION = 7.5f;

        // Сохраняем исходное состояние всех коллайдеров удерживаемого предмета.
        // При отпускании восстанавливаем каждый коллайдер отдельно.
        private class HeldColliderState
        {
            public Collider collider;
            public bool wasEnabled;
        }

        private readonly List<HeldColliderState> _heldColliderStates =
            new List<HeldColliderState>();

        // Последний предмет, который V отпустила или бросила.
        private Rigidbody _lastReleasedObjectRb;
        private float _lastReleasedObjectAt = -10f;
        private float pickupRange = 3f;
        private float grabHeight = 1.2f;

        private static readonly HashSet<GameObject> _seenRoots = new HashSet<GameObject>();

        // Реестр физических объектов, обнаруженных сканером.
        // Ключ — Unity Instance ID.
        private readonly Dictionary<int, Rigidbody> _objectRegistry =
            new Dictionary<int, Rigidbody>();

        // Адресное перемещение к выбранному объекту.
        private Transform _approachTarget;
        private string _approachObjectId = "";
        private string _approachOutcome = "idle";
        private float _approachStartedAt = 0f;

        private const float OBJECT_APPROACH_TIMEOUT = 45f;
        private const float OBJECT_APPROACH_STOP_DISTANCE = 1.4f;
        private const float OBJECT_PICKUP_DISTANCE = 2.4f;

        private bool inCar = false;
        private GameObject currentCar;
        private Transform carSeat;
        private Vector3 preCarPosition;
        private Vector3 preCarScale;

        private class DoorAction
        {
            public GameObject door;
            public Rigidbody rb;
            public Vector3 localTorque;
            public float endTime;
            public bool open;
            public Rigidbody carRb; // <--- НОВОЕ
        }
        private List<DoorAction> activeDoorActions = new List<DoorAction>();

        // ═══ УПРАВЛЕНИЕ ═══
        private Component _axis;
        private Component _drivetrain;
        private int _currentGear = 1;
        private float _targetThrottle = 0f;
        private float _targetBrake = 0f;
        private float _targetSteer = 0f;
        private bool _injectInput = false;
        private bool _driveSequenceRunning = false;

        // ═══ АВТОШИФТ ═══
        private bool _autoShiftEnabled = false;
        private float _nextShiftAllowedAt = 0f;
        private float _firstGearEnteredAt = 0f;
        private float _rpmAboveThresholdSince = -1f;
        private float _rpmBelowThresholdSince = -1f;
        private const int GEAR_NEUTRAL = 1;
        private const int GEAR_FIRST = 2;
        private const int GEAR_LAST = 6;
        private const float SHIFT_UP_RPM = 5000f;
        private const float SHIFT_UP_RPM_FIRST = 5500f;
        private const float SHIFT_DOWN_RPM = 1500f;
        private const float SHIFT_COOLDOWN = 0.6f;
        private const float FIRST_GEAR_MIN_TIME = 2.5f;
        private const float RPM_STABLE_TIME = 0.4f;

        // ═══ ЗАПИСЬ МАРШРУТА ═══
        private bool _recording = false;
        private List<Vector3> _recordedPoints = new List<Vector3>();
        private GameObject _recordingCar = null;
        private List<Vector3> _pendingRoutePoints = new List<Vector3>();
        private bool _awaitingRouteName = false;
        private const float REC_MIN_DIST = 6f;

        // ═══ ВОСПРОИЗВЕДЕНИЕ ═══
        private bool _playingRoute = false;
        private bool _playbackPreparing = false;
        private List<Vector3> _playPoints = new List<Vector3>();
        private int _playIndex = 0;
        private int _playMinIndex = 0; // Не позволяем поиску ближайшей точки возвращаться до выбранной точки входа
        private float _navT0 = 0f;
        private float _navStartedAt = 0f;
        private float _currentSteer = 0f;
        private string _lastSavedRoutePath = "";
        private List<Vector3> _nextSegmentPreview = new List<Vector3>();

        // ═══ ГРАФ ═══
        private RouteGraph _graph = new RouteGraph();
        private RouteNavigator _navigator;
        private bool _navMultiSegment = false;
        private bool _segmentFinished = false;

        // Состояние навигации для V-Agent.
        private string _navigationOutcome = "idle";

        // Данные для агентного наблюдателя.
        private string _navigationDestination = "";
        private float _lastRouteDeviationMeters = -1f;

        private static Dictionary<string, FieldInfo> _fieldCache = new Dictionary<string, FieldInfo>();

        public bool IsFollowing => following;
        public bool HasItem => heldObject != null;
        public string HeldObjectId =>
    heldRb != null
        ? "object_" + heldRb.gameObject.GetInstanceID()
        : "";

        public string HeldObjectName =>
            heldObject != null
                ? CleanName(heldObject.name)
                : "";

        public string LastReleasedObjectId =>
            _lastReleasedObjectRb != null
                ? "object_" + _lastReleasedObjectRb.gameObject.GetInstanceID()
                : "";

        public Vector3 LastReleasedObjectPosition =>
            _lastReleasedObjectRb != null
                ? _lastReleasedObjectRb.position
                : Vector3.zero;

        public string ObjectApproachOutcome => _approachOutcome;

        public string ObjectApproachId => _approachObjectId;
        public bool InCar => inCar;
        // Состояние игрока
        public bool PlayerIsPassenger =>
            _playerSitting && _playerCar != null && _playerSeat != null;
        // Положение V в автомобиле
        public bool IsEnteringCar => _isEnteringCar;

        public string SeatRole
        {
            get
            {
                if (!inCar)
                    return "none";

                return _isPassengerSeat ? "passenger" : "driver";
            }
        }
        public bool IsDrivingSequence => _driveSequenceRunning;
        public bool IsAutoShiftEnabled => _autoShiftEnabled;
        public bool IsRecording => _recording;
        public bool IsPlayingRoute => _playingRoute;
        public string NavigationOutcome => _navigationOutcome;

        public bool IsNavigationActive =>
            _playingRoute ||
            _playbackPreparing ||
            _isFinishingRoute;
        public bool IsAwaitingRouteName => _awaitingRouteName;

        // ═══ СОСТОЯНИЕ ДЛЯ AI-АГЕНТА ═══

        public bool IsRoutePreparing => _playbackPreparing;
        public bool IsFinishingRoute => _isFinishingRoute;
        public bool HasCurrentVehicle => currentCar != null;

        public string CurrentRouteName => _currentRouteName ?? "";
        public string NavigationDestination => _navigationDestination ?? "";

        public int RoutePointIndex => _playIndex;
        public int RoutePointCount => _playPoints != null ? _playPoints.Count : 0;

        public float NearestRoutePointDistanceMeters => _lastRouteDeviationMeters;

        public Vector3 VehiclePosition =>
            currentCar != null ? currentCar.transform.position : Vector3.zero;

        public float VehicleSpeedKmh
        {
            get
            {
                Rigidbody rb = currentCar != null
                    ? currentCar.GetComponent<Rigidbody>()
                    : null;

                return rb != null ? rb.velocity.magnitude * 3.6f : 0f;
            }
        }

        public float VehicleVerticalSpeedMps
        {
            get
            {
                Rigidbody rb = currentCar != null
                    ? currentCar.GetComponent<Rigidbody>()
                    : null;

                return rb != null ? rb.velocity.y : 0f;
            }
        }

        public float VehicleUpDot
        {
            get
            {
                return currentCar != null
                    ? Vector3.Dot(currentCar.transform.up, Vector3.up)
                    : 1f;
            }
        }

        public float VehicleTiltDegrees
        {
            get
            {
                return currentCar != null
                    ? Vector3.Angle(currentCar.transform.up, Vector3.up)
                    : 0f;
            }
        }

        private float _originalCCHeight = 1.8f;
        private Vector3 _originalCCCenter = new Vector3(0f, 0.9f, 0f);

        private Animator _animator;

        // ═══ КОСТИ ДЛЯ ВРАЩЕНИЯ РУЛЯ ═══
        private Transform _lClavicle, _lUpperArm, _lForearm, _lHand;
        private Transform _steeringWheel; // Ссылка на руль машины
        private Vector3 _lUpperArmBasePos;

        private bool _isEnteringCar = false;

        private bool _isFinishingRoute = false;

        private bool _navigationCancelRequested = false;

        private bool _isWalkingToPoint = false;

        void Start()
        {
            cc = GetComponent<CharacterController>();
            ConfigureTraversal();
            _animator = GetComponentInChildren<Animator>();

            if (_animator == null) Debug.Log("[V] ❌ Animator не найден!");
            else if (_animator.runtimeAnimatorController == null) Debug.Log("[V] ❌ Animator есть, но Controller пустой (не запакован в бандл)!");
            else Debug.Log("[V] ✅ Animator работает! Controller: " + _animator.runtimeAnimatorController.name);

            var p = GameObject.Find("PLAYER");
            if (p != null) playerTransform = p.transform;

            var gp = new GameObject("GrabPoint");
            gp.transform.SetParent(transform, false);
            gp.transform.localPosition = new Vector3(0f, grabHeight, 0.5f);
            grabPoint = gp.transform;

            SetupImpactSensor();
        }

        private void ConfigureTraversal()
        {
            if (cc == null)
                return;

            // stepOffset задаёт максимальную высоту небольшого уступа,
            // а slopeLimit — максимальный угол поверхности, на которую
            // CharacterController разрешает подниматься.
            float safeMaxStep = Mathf.Max(0.05f, cc.height - 0.05f);
            float targetStepOffset = Mathf.Min(0.4f, safeMaxStep);

            if (cc.stepOffset < targetStepOffset)
                cc.stepOffset = targetStepOffset;

            // В гараже есть крутые наклонные поверхности (примерно до 70°).
            // Оставляем небольшой запас, чтобы V могла подняться по ним.
            const float targetSlopeLimit = 80f;
            if (cc.slopeLimit < targetSlopeLimit)
                cc.slopeLimit = targetSlopeLimit;

            Debug.Log(
                "[V][MOVEMENT] Настройки преодоления поверхности: height=" +
                cc.height.ToString("F2") +
                ", stepOffset=" + cc.stepOffset.ToString("F2") +
                ", slopeLimit=" + cc.slopeLimit.ToString("F1") + "°"
            );
        }

        private void SetupImpactSensor()
        {
            if (cc == null)
                return;

            GameObject sensorObject = new GameObject("VImpactSensor");
            sensorObject.layer = gameObject.layer;
            sensorObject.transform.SetParent(transform, false);

            Rigidbody sensorRb = sensorObject.AddComponent<Rigidbody>();
            sensorRb.isKinematic = true;
            sensorRb.useGravity = false;

            _impactTrigger = sensorObject.AddComponent<CapsuleCollider>();
            _impactTrigger.isTrigger = true;
            _impactTrigger.direction = 1;
            _impactTrigger.center = cc.center;
            _impactTrigger.radius = cc.radius + 0.08f;
            _impactTrigger.height = Mathf.Max(
                cc.height + 0.25f,
                _impactTrigger.radius * 2f
            );

            VImpactSensor sensor = sensorObject.AddComponent<VImpactSensor>();
            sensor.Initialize(this);

            UpdateImpactSensorState();

            Debug.Log("[V][PHYSICS] Датчик внешних столкновений создан.");
        }

        private void UpdateImpactSensorState()
        {
            if (_impactTrigger == null)
                return;

            bool shouldBeEnabled =
                cc != null &&
                cc.enabled &&
                !inCar &&
                !_isEnteringCar;

            if (_impactTrigger.enabled != shouldBeEnabled)
                _impactTrigger.enabled = shouldBeEnabled;

            if (!shouldBeEnabled)
                _externalVelocity = Vector3.zero;
        }

        private void ApplyExternalVelocity(float deltaTime)
        {
            if (_externalVelocity.sqrMagnitude <= 0.0001f)
            {
                _externalVelocity = Vector3.zero;
                return;
            }

            _externalVelocity = Vector3.MoveTowards(
                _externalVelocity,
                Vector3.zero,
                EXTERNAL_IMPACT_DECELERATION * deltaTime
            );
        }

        public void RegisterExternalImpact(Rigidbody body)
        {
            if (body == null || body.isKinematic)
                return;

            if (inCar || _isEnteringCar || cc == null || !cc.enabled)
                return;

            if (body == heldRb || body.gameObject == gameObject)
                return;

            // Не считаем собственный бросок V ударом по самой себе.
            if (body == _lastReleasedObjectRb &&
                Time.time - _lastReleasedObjectAt < 0.4f)
            {
                return;
            }

            Vector3 incomingVelocity = body.velocity;
            float impactSpeed = incomingVelocity.magnitude;
            if (impactSpeed < 0.75f)
                return;

            float mass = Mathf.Max(0f, body.mass);
            float transferredSpeed =
                impactSpeed * (mass / (mass + EXTERNAL_IMPACT_MASS)) * 1.4f;

            if (transferredSpeed < 0.15f)
                return;

            transferredSpeed = Mathf.Min(
                transferredSpeed,
                EXTERNAL_IMPACT_MAX_SPEED
            );

            _externalVelocity += incomingVelocity.normalized * transferredSpeed;

            if (_externalVelocity.magnitude > EXTERNAL_IMPACT_MAX_SPEED)
            {
                _externalVelocity =
                    _externalVelocity.normalized * EXTERNAL_IMPACT_MAX_SPEED;
            }

            Debug.Log(
                "[V][PHYSICS] Внешний удар: объект=" + body.name +
                ", скорость=" + impactSpeed.ToString("F1") +
                ", импульс V=" + transferredSpeed.ToString("F1")
            );
        }

        void Update()
        {
            if (_injectInput && _axis != null)
            {
                SetField(_axis, "throttleInput", _targetThrottle);
                SetField(_axis, "throttle", _targetThrottle);
                SetField(_axis, "brakeInput", _targetBrake);
                SetField(_axis, "brake", _targetBrake);
                SetField(_axis, "steerInput", _targetSteer);
                SetField(_axis, "steering", _targetSteer);
            }

            // ═══ ЖИВОЕ ОБНОВЛЕНИЕ ПОЗИЦИИ И МАСШТАБА СИДЕНЬЯ ═══
            if (inCar && currentCar != null && carSeat != null)
            {
                Vector3 offset = _isPassengerSeat ? PassengerSeatOffset : DriverSeatOffset;
                transform.localPosition = _seatLocalPos + offset;

                // Применяем масштаб из ползунков
                float currentScale = _isPassengerSeat ? PassengerSeatScale : DriverSeatScale;
                transform.localScale = new Vector3(preCarScale.x * currentScale, preCarScale.y * currentScale, preCarScale.z * currentScale);
            }
            // ═══ ПРИНУДИТЕЛЬНОЕ УДЕРЖАНИЕ ИГРОКА НА СИДЕНЬЕ ═══
            if (_playerSitting && _playerTransform != null && _playerCar != null && _playerSeat != null)
            {
                // 1. Держим позицию
                Vector3 seatLocalPos = _playerCar.transform.InverseTransformPoint(_playerSeat.position);
                Vector3 finalLocalPos = seatLocalPos + PlayerPassengerOffset;
                _playerTransform.position = _playerCar.transform.TransformPoint(finalLocalPos);

                // 2. Крутим игрока ТОЛЬКО вместе с машиной, не сбрасывая мышь
                Quaternion carDelta = _playerCar.transform.rotation * Quaternion.Inverse(_lastCarRot);
                _playerTransform.rotation = carDelta * _playerTransform.rotation;
                _lastCarRot = _playerCar.transform.rotation;
            }

            UpdateAutoShift();
            UpdateRecording();
            UpdateRoutePlayback();
            UpdateDoorActions();
            UpdateImpactSensorState();

            // ═══ ОПРЕДЕЛЯЕМ, ИДЁТ ЛИ ОНА ПЕШКОМ (ДЛЯ АНИМАТОРА) ═══
            bool isWalking = false;
            if (!inCar && !_isEnteringCar && following && playerTransform != null)
            {
                Vector3 dirToPlayer = playerTransform.position - transform.position;
                dirToPlayer.y = 0f;
                if (dirToPlayer.magnitude > 2.5f)
                {
                    isWalking = true;
                }
            }

            // НОВОЕ: идёт к двери машины через корутину MoveToPoint
            if (_isWalkingToPoint)
                isWalking = true;

            // ═══ ПЕРЕДАЕМ СКОРОСТЬ В АНИМАТОР ═══
            if (_animator != null)
            {
                _animator.SetFloat("Speed", isWalking ? WalkSpeed : 0f);
            }

            if (heldObject != null && grabPoint != null)
                heldObject.transform.position = grabPoint.position;

            if (_isEnteringCar) return;
            if (cc == null) return;
            if (inCar) return;

            if (cc.isGrounded && verticalVelocity < 0f)
                verticalVelocity = -2f;

            if (Time.time - jumpRequestedAt < 0.1f && cc.isGrounded)
            {
                verticalVelocity = 6f;
                jumpRequestedAt = -10f;
            }

            verticalVelocity += gravity * Time.deltaTime;
            Vector3 move = new Vector3(0f, verticalVelocity, 0f);

            // ═══ АДРЕСНЫЙ ПОДХОД К ПРЕДМЕТУ ═══
            if (_approachOutcome == "running")
            {
                if (_approachTarget == null)
                {
                    FinishObjectApproach("failed");
                }
                else if (
                    Time.time - _approachStartedAt >=
                    OBJECT_APPROACH_TIMEOUT
                )
                {
                    FinishObjectApproach("timeout");
                }
                else
                {
                    Vector3 dir =
                        _approachTarget.position - transform.position;

                    dir.y = 0f;

                    float distance = dir.magnitude;

                    if (distance <= OBJECT_APPROACH_STOP_DISTANCE)
                    {
                        FinishObjectApproach("succeeded");
                    }
                    else if (distance > 0.1f)
                    {
                        dir.Normalize();

                        Quaternion look = Quaternion.LookRotation(dir);

                        transform.rotation = Quaternion.Slerp(
                            transform.rotation,
                            look,
                            Time.deltaTime * 6f
                        );

                        move += dir * WalkSpeed;
                    }
                }
            }
            // ═══ ОБЫЧНОЕ СЛЕДОВАНИЕ ЗА ИГРОКОМ ═══
            else if (following && playerTransform != null)
            {
                Vector3 dir =
                    playerTransform.position - transform.position;

                dir.y = 0f;

                if (dir.magnitude > 2.5f)
                {
                    dir.Normalize();

                    Quaternion look = Quaternion.LookRotation(dir);

                    transform.rotation = Quaternion.Slerp(
                        transform.rotation,
                        look,
                        Time.deltaTime * 6f
                    );

                    move += dir * WalkSpeed;
                }
            }

            float deltaTime = Time.deltaTime;
            cc.Move((move + _externalVelocity) * deltaTime);
            ApplyExternalVelocity(deltaTime);
        }

        void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (heldObject != null && hit.gameObject == heldObject) return;
            Rigidbody body = hit.collider.attachedRigidbody;
            if (body == null || body.isKinematic) return;
            if (hit.moveDirection.y < -0.3f) return;
            Vector3 pushDir = new Vector3(hit.moveDirection.x, 0f, hit.moveDirection.z);
            body.AddForce(pushDir * 15f, ForceMode.Force);
        }

        public void SetFollow(bool v) { following = v; }
        public void ToggleFollow() { following = !following; }
        public void Jump() { jumpRequestedAt = Time.time; }

        private bool BeginHoldingObject(Rigidbody rb)
        {
            if (rb == null || heldObject != null)
                return false;

            GameObject item = rb.gameObject;
            if (item == null || item == gameObject)
                return false;

            heldObject = item;
            heldRb = rb;
            _lastReleasedObjectRb = null;

            _heldColliderStates.Clear();

            Collider[] colliders = item.GetComponentsInChildren<Collider>(true);
            foreach (Collider itemCollider in colliders)
            {
                if (itemCollider == null)
                    continue;

                _heldColliderStates.Add(new HeldColliderState
                {
                    collider = itemCollider,
                    wasEnabled = itemCollider.enabled
                });

                itemCollider.enabled = false;
            }

            heldRb.isKinematic = true;
            heldRb.useGravity = false;

            if (grabPoint != null)
                heldObject.transform.position = grabPoint.position;

            Debug.Log(
                "[V][OBJECT] Взяла предмет, отключено коллайдеров: " +
                _heldColliderStates.Count
            );

            return true;
        }

        private void RestoreHeldObjectColliders()
        {
            int restored = 0;

            foreach (HeldColliderState state in _heldColliderStates)
            {
                if (state == null || state.collider == null)
                    continue;

                state.collider.enabled = state.wasEnabled;
                restored++;
            }

            _heldColliderStates.Clear();

            Debug.Log(
                "[V][OBJECT] Восстановлено коллайдеров предмета: " +
                restored
            );
        }

        public bool Pickup()
        {
            if (heldObject != null) return false;

            Vector3 origin = transform.position + Vector3.up * grabHeight;
            RaycastHit hit;
            if (Physics.Raycast(origin, transform.forward, out hit, pickupRange))
            {
                Rigidbody rb = hit.collider.attachedRigidbody;
                if (rb != null && rb.gameObject != gameObject && rb.mass < 100f)
                    return BeginHoldingObject(rb);
            }

            return false;
        }

        public bool PickupNearby(float radius = 4f)
        {
            if (heldObject != null) return false;

            Collider[] cols = Physics.OverlapSphere(
                transform.position + Vector3.up * grabHeight,
                radius
            );

            Rigidbody closest = null;
            float bestDist = float.MaxValue;

            foreach (var c in cols)
            {
                var rb = c.attachedRigidbody;
                if (rb == null || rb.gameObject == gameObject || rb.mass > 100f)
                    continue;

                float d = Vector3.Distance(transform.position, rb.transform.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    closest = rb;
                }
            }

            return closest != null && BeginHoldingObject(closest);
        }

        // ========================================================
        // АДРЕСНОЕ ВЗАИМОДЕЙСТВИЕ С ОБЪЕКТАМИ
        // ========================================================

        public bool ApproachObject(string objectId)
        {
            if (inCar ||
                _isEnteringCar ||
                cc == null ||
                !cc.enabled ||
                heldObject != null ||
                _approachOutcome == "running")
                return false;

            Rigidbody targetRb;

            if (!TryGetRegisteredObject(objectId, out targetRb))
                return false;

            if (targetRb.mass >= 100f)
                return false;

            // Не пытаемся одновременно следовать за игроком.
            following = false;

            _approachTarget = targetRb.transform;
            _approachObjectId = objectId;
            _approachStartedAt = Time.time;
            _approachOutcome = "running";
            _isWalkingToPoint = true;

            Debug.Log("[V][OBJECT] Подхожу к " + targetRb.name);

            return true;
        }

        public void CancelApproachObject()
        {
            if (_approachOutcome == "running")
                FinishObjectApproach("cancelled");
        }

        private void FinishObjectApproach(string outcome)
        {
            _approachOutcome = outcome;
            _approachTarget = null;
            _isWalkingToPoint = false;

            Debug.Log(
                "[V][OBJECT] Подход завершён: " +
                _approachObjectId +
                ", статус=" + outcome
            );
        }

        public bool PickupObject(string objectId)
        {
            if (heldObject != null ||
                inCar ||
                _isEnteringCar)
                return false;

            Rigidbody targetRb;

            if (!TryGetRegisteredObject(objectId, out targetRb))
                return false;

            if (targetRb.mass >= 100f)
                return false;

            float distance = Vector3.Distance(
                transform.position + Vector3.up * grabHeight,
                targetRb.worldCenterOfMass
            );

            if (distance > OBJECT_PICKUP_DISTANCE)
            {
                Debug.Log(
                    "[V][OBJECT] Предмет слишком далеко для подбора: " +
                    distance.ToString("F2") + "м"
                );

                return false;
            }


            // Поднимаем строго выбранный Rigidbody и отключаем
            // все его коллайдеры до отпускания предмета.
            bool pickedUp = BeginHoldingObject(targetRb);

            bool success =
                pickedUp &&
                heldObject != null &&
                heldRb != null &&
                HeldObjectId == objectId;

            if (success)
            {
                Debug.Log(
                    "[V][OBJECT] Поднят предмет: " +
                    HeldObjectName +
                    " [" + HeldObjectId + "]"
                );
            }

            return success;
        }

        public void Drop()
        {
            if (heldObject == null) return;

            Rigidbody releasedRb = heldRb != null
                ? heldRb
                : heldObject.GetComponent<Rigidbody>();

            _lastReleasedObjectRb = releasedRb;
            _lastReleasedObjectAt = Time.time;

            // Сначала возвращаем предмету исходные коллайдеры,
            // затем снова включаем его физику.
            RestoreHeldObjectColliders();

            if (releasedRb != null)
            {
                releasedRb.isKinematic = false;
                releasedRb.useGravity = true;
                releasedRb.WakeUp();
            }

            heldObject = null;
            heldRb = null;
        }

        public void Throw(float force = 12f)
        {
            if (heldObject == null) return;

            Rigidbody rb = heldRb != null
                ? heldRb
                : heldObject.GetComponent<Rigidbody>();

            _lastReleasedObjectRb = rb;
            _lastReleasedObjectAt = Time.time;

            // Возвращаем коллайдеры перед броском, чтобы предмет
            // снова полноценно участвовал в столкновениях.
            RestoreHeldObjectColliders();

            heldObject = null;
            heldRb = null;

            if (rb != null)
            {
                rb.isKinematic = false;
                rb.useGravity = true;
                rb.WakeUp();

                Vector3 dir =
                    (transform.forward + Vector3.up * 0.3f).normalized;

                rb.AddForce(dir * force, ForceMode.Impulse);
            }
        }

        public string InteractWithNearest(string action, float radius = 5f)
        {
            return InteractWithNearestInternal(action, radius, false);
        }

        // Новый метод специально для V-Agent.
        // Ищет только двери, исключая капот.
        public string InteractWithNearestDoor(string action, float radius = 5f)
        {
            return InteractWithNearestInternal(action, radius, true);
        }

        private string InteractWithNearestInternal(
            string action,
            float radius,
            bool doorsOnly)
        {
            Collider[] cols = Physics.OverlapSphere(
                transform.position + Vector3.up * 0.9f,
                radius
            );

            GameObject best = null;
            float bestDist = float.MaxValue;
            string bestName = "";

            foreach (var c in cols)
            {
                if (c == null) continue;

                GameObject obj = c.gameObject;
                if (obj == gameObject) continue;

                GameObject candidate = FindDoorObject(obj);
                if (candidate == null) continue;

                string ln = candidate.name.ToLowerInvariant();

                bool isDoor = ln.Contains("door");
                bool isHood = ln.Contains("hood");

                if (!isDoor && !isHood) continue;

                // Для V-Agent разрешаем только двери.
                if (doorsOnly && !isDoor) continue;

                float d = Vector3.Distance(
                    transform.position,
                    candidate.transform.position
                );

                if (d < bestDist)
                {
                    bestDist = d;
                    best = candidate;
                    bestName = isDoor ? "дверь" : "капот";
                }
            }

            if (best == null)
                return "рядом нет подходящих дверей";

            bool open = action == "OPEN";

            bool ok = MoveDoorPhysical(best, open);

            if (ok)
            {
                return "OK: " + bestName + " (" + best.name + ") " +
                       (open ? "открываю" : "закрываю");
            }

            return "не смогла двинуть " + best.name;
        }

        private GameObject FindDoorObject(GameObject start)
        {
            Transform cur = start.transform;
            int safety = 0;
            while (cur != null && safety < 8)
            {
                string ln = cur.name.ToLower();
                if ((ln.Contains("door") || ln.Contains("hood")) && cur.GetComponent<HingeJoint>() != null)
                    return cur.gameObject;
                cur = cur.parent; safety++;
            }
            return null;
        }

        // ═══════════════════════════════════════════════════
        //  PLAYMAKER FSM — УПРАВЛЕНИЕ ДВЕРЯМИ SATSUMA
        // ═══════════════════════════════════════════════════

        // Чтение свойств и полей PlayMaker через Reflection
        private static object GetPlayMakerMember(object instance, string name)
        {
            if (instance == null) return null;

            BindingFlags flags =
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic;

            try
            {
                PropertyInfo p = instance.GetType().GetProperty(name, flags);

                if (p != null)
                    return p.GetValue(instance, null);

                FieldInfo f = instance.GetType().GetField(name, flags);

                if (f != null)
                    return f.GetValue(instance);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[V][DOOR FSM] Reflection: " + e.Message);
            }

            return null;
        }


        // Ищем FSM с именем Use на конкретной двери
        private Component FindDoorUseFsm(GameObject door)
        {
            Component[] components = door.GetComponents<Component>();

            foreach (Component c in components)
            {
                if (c == null) continue;

                if (c.GetType().Name != "PlayMakerFSM")
                    continue;

                object fsmName = GetPlayMakerMember(c, "FsmName");

                if (fsmName != null &&
                    string.Equals(
                        fsmName.ToString(),
                        "Use",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return c;
                }
            }

            return null;
        }


        // Переключаем FSM на нужное состояние
        private bool SwitchPlayMakerState(Component fsmComponent, string stateName)
        {
            object fsm = GetPlayMakerMember(fsmComponent, "Fsm");

            if (fsm == null)
            {
                Debug.LogWarning("[V][DOOR FSM] Fsm не найден!");
                return false;
            }

            Array states = GetPlayMakerMember(fsm, "States") as Array;

            if (states == null)
            {
                Debug.LogWarning("[V][DOOR FSM] States не найдены!");
                return false;
            }

            object targetState = null;

            foreach (object state in states)
            {
                object name = GetPlayMakerMember(state, "Name");

                if (name != null && name.ToString() == stateName)
                {
                    targetState = state;
                    break;
                }
            }

            if (targetState == null)
            {
                Debug.LogWarning(
                    "[V][DOOR FSM] Состояние не найдено: " + stateName);
                return false;
            }

            BindingFlags flags =
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic;

            MethodInfo[] methods = fsm.GetType().GetMethods(flags);

            foreach (MethodInfo method in methods)
            {
                if (method.Name != "SwitchState")
                    continue;

                ParameterInfo[] parameters = method.GetParameters();

                if (parameters.Length != 1)
                    continue;

                if (!parameters[0].ParameterType.IsAssignableFrom(
                        targetState.GetType()))
                    continue;

                try
                {
                    method.Invoke(fsm, new object[] { targetState });

                    Debug.Log(
                        "[V][DOOR FSM] Переключено состояние: " + stateName);

                    return true;
                }
                catch (Exception e)
                {
                    Debug.LogWarning(
                        "[V][DOOR FSM] Ошибка переключения: " + e);
                    return false;
                }
            }

            Debug.LogWarning(
                "[V][DOOR FSM] Метод SwitchState не найден!");

            return false;
        }


        // Удаляем только старые бесконечные замки V
        private void RemoveLegacyDoorFixedJoints(GameObject door)
        {
            FixedJoint[] joints = door.GetComponents<FixedJoint>();

            int removed = 0;

            foreach (FixedJoint joint in joints)
            {
                if (joint == null) continue;

                // Штатные замки игры имеют конечную прочность.
                // Старый замок V имел Infinity у обоих параметров.
                if (float.IsInfinity(joint.breakForce) &&
                    float.IsInfinity(joint.breakTorque))
                {
                    UnityEngine.Object.Destroy(joint);
                    removed++;
                }
            }

            if (removed > 0)
            {
                Debug.Log(
                    "[V][DOOR FSM] Удалено старых замков V: " + removed);
            }
        }


        // Переключаем FSM на следующем кадре,
        // чтобы Unity успела удалить старые FixedJoint
        private IEnumerator SwitchDoorFsmNextFrame(
            GameObject door,
            Component fsmComponent,
            string stateName)
        {
            yield return null;

            if (door == null || fsmComponent == null)
                yield break;

            SwitchPlayMakerState(fsmComponent, stateName);
        }

        public bool MoveDoorPhysical(GameObject door, bool open)
        {
            if (door == null) return false;
            var rb = door.GetComponent<Rigidbody>(); if (rb == null) return false;
            var hinge = door.GetComponent<HingeJoint>(); if (hinge == null) return false;

            // ═══ ШТАТНОЕ УПРАВЛЕНИЕ SATSUMA ЧЕРЕЗ PLAYMAKER ═══

            Component useFsm = FindDoorUseFsm(door);

            if (useFsm != null)
            {
                // Удаляем старые задачи физического управления V
                for (int i = activeDoorActions.Count - 1; i >= 0; i--)
                {
                    if (activeDoorActions[i].door == door)
                    {
                        activeDoorActions.RemoveAt(i);
                    }
                }

                // Удаляем только старый бесконечный замок V
                RemoveLegacyDoorFixedJoints(door);

                // Выбираем штатное состояние игры
                string doorName = door.name.ToLowerInvariant();

                string closeState = doorName.Contains("right")
                    ? "Open door 3"
                    : "Open door 2";

                string targetState = open
                    ? "Open door"
                    : closeState;

                Debug.Log(
                    "[V][DOOR FSM] " +
                    (open ? "Открываю " : "Закрываю ") +
                    door.name);

                StartCoroutine(
                    SwitchDoorFsmNextFrame(door, useFsm, targetState));

                return true;
            }

            // Если на объекте нет FSM Use,
            // ниже продолжает работать старый физический механизм.

            // ═══ ОЧИЩАЕМ СТАРЫЕ ЗАДАЧИ ДЛЯ ЭТОЙ ДВЕРИ ═══
            for (int i = activeDoorActions.Count - 1; i >= 0; i--)
            {
                if (activeDoorActions[i].door == door)
                {
                    activeDoorActions.RemoveAt(i);
                }
            }

            var fj = door.GetComponent<FixedJoint>();
            if (fj != null) { UnityEngine.Object.Destroy(fj); } // Снимаем замок

            rb.isKinematic = false;
            rb.WakeUp();

            // ═══ ИЩЕМ RIGIDBODY МАШИНЫ (КУЗОВА) ═══
            Rigidbody carRb = null;
            if (currentCar != null)
            {
                carRb = currentCar.GetComponent<Rigidbody>();
            }

            // Страховка: если вдруг currentCar не имеет Rigidbody, ищем вверх по иерархии, пропуская саму дверь
            if (carRb == null)
            {
                Transform p = door.transform.parent;
                while (p != null)
                {
                    Rigidbody prb = p.GetComponent<Rigidbody>();
                    if (prb != null && prb != rb)
                    {
                        carRb = prb;
                        break;
                    }
                    p = p.parent;
                }
            }

            string ln = door.name.ToLower();
            bool isRight = ln.Contains("right"), isHood = ln.Contains("hood");
            Vector3 localTorque; JointLimits limits = hinge.limits;

            if (isHood)
            {
                limits.min = -87; limits.max = 0;
                localTorque = open ? new Vector3(-30f, 0, 0) : new Vector3(50f, 0, 0);
            }
            else if (isRight)
            {
                limits.min = -80; limits.max = 0;
                localTorque = open ? new Vector3(0, 0, -120f) : new Vector3(0, 0, 145f);
            }
            else
            {
                limits.min = 0; limits.max = 80;
                localTorque = open ? new Vector3(0, 0, 120f) : new Vector3(0, 0, -145f);
            }

            hinge.limits = limits;

            activeDoorActions.Add(new DoorAction { door = door, rb = rb, localTorque = localTorque, endTime = Time.time + (open ? 3.5f : 5.5f), open = open, carRb = carRb });
            return true;
        }

        private void UpdateDoorActions()
        {
            for (int i = activeDoorActions.Count - 1; i >= 0; i--)
            {
                var act = activeDoorActions[i];
                if (act.door == null || act.rb == null) { activeDoorActions.RemoveAt(i); continue; }

                // ═══ ЕСЛИ ВРЕМЯ ВЫШЛО — ЗАВЕРШАЕМ ═══
                if (Time.time > act.endTime)
                {
                    if (!act.open) // Если закрывали, вешаем замок
                    {
                        var f = act.door.GetComponent<FixedJoint>();
                        if (f == null)
                        {
                            try
                            {
                                f = act.door.AddComponent<FixedJoint>();

                                // Привязываем к кузову машины
                                if (act.carRb != null)
                                {
                                    f.connectedBody = act.carRb;
                                }

                                // Делаем замок неразрывным, чтобы дверь не отвалилась на кочках
                                f.breakForce = Mathf.Infinity;
                                f.breakTorque = Mathf.Infinity;
                            }
                            catch { }
                        }
                    }
                    activeDoorActions.RemoveAt(i);
                    continue;
                }

                // ═══ ПРИМЕНЯЕМ СИЛУ ═══
                Vector3 wt = act.door.transform.TransformDirection(act.localTorque);
                act.rb.AddTorque(wt, ForceMode.Force);
            }
        }

        // reflection
        private static FieldInfo GetCachedField(Type t, string name)
        {
            string key = t.FullName + "." + name;
            if (_fieldCache.ContainsKey(key)) return _fieldCache[key];
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            _fieldCache[key] = f; return f;
        }
        private static void SetField(Component comp, string name, object value)
        {
            if (comp == null) return;
            try { var f = GetCachedField(comp.GetType(), name); if (f != null) f.SetValue(comp, value); } catch { }
        }
        private static object GetField(Component comp, string name)
        {
            if (comp == null) return null;
            try { var f = GetCachedField(comp.GetType(), name); if (f != null) return f.GetValue(comp); } catch { }
            return null;
        }
        private static Component FindComponentByTypeName(GameObject go, string typeName)
        {
            if (go == null) return null;
            foreach (var c in go.GetComponents<Component>())
                if (c != null && c.GetType().Name == typeName) return c;
            return null;
        }

        private void CacheCarComponents()
        {
            if (currentCar == null) return;
            _axis = FindComponentByTypeName(currentCar, "AxisCarController");
            _drivetrain = FindComponentByTypeName(currentCar, "Drivetrain");
        }
        private void CacheCheck() { if (_axis == null || _drivetrain == null) CacheCarComponents(); }

        public bool SetThrottle(float v) { CacheCheck(); if (_axis == null) return false; _targetThrottle = Mathf.Clamp01(v); _injectInput = true; return true; }
        public bool SetBrake(float v) { CacheCheck(); if (_axis == null) return false; _targetBrake = Mathf.Clamp01(v); _injectInput = true; return true; }
        public bool SetSteer(float v) { CacheCheck(); if (_axis == null) return false; _targetSteer = Mathf.Clamp(v, -1f, 1f); _injectInput = true; return true; }
        public void ReleaseInput() { _targetThrottle = 0f; _targetBrake = 0f; _targetSteer = 0f; _injectInput = false; }

        public bool ShiftTo(int gear)
        {
            CacheCheck();
            if (_drivetrain == null) return false;
            try
            {
                var m = _drivetrain.GetType().GetMethod("Shift", BindingFlags.Public | BindingFlags.Instance);
                if (m == null) return false;
                m.Invoke(_drivetrain, new object[] { gear });
                _currentGear = gear;
                if (gear == GEAR_FIRST) _firstGearEnteredAt = Time.time;
                return true;
            }
            catch { return false; }
        }

        public float GetRpm() { object r = GetField(_drivetrain, "rpm"); if (r is float) return (float)r; if (r is double) return (float)(double)r; return 0f; }
        public int GetGear() { object g = GetField(_drivetrain, "gear"); if (g is int) return (int)g; return _currentGear; }

        public void DebugThrottle(float v) { SetThrottle(v); Debug.Log("[V] Throttle " + v); }
        public void DebugBrake(float v) { SetBrake(v); Debug.Log("[V] Brake " + v); }
        public void DebugSteer(float v) { SetSteer(v); Debug.Log("[V] Steer " + v); }
        public void DebugShift(int g) { ShiftTo(g); }
        public void DebugLogState() { CacheCheck(); Debug.Log("[V] STATE: gear=" + GetGear() + " rpm=" + GetRpm().ToString("F0") + " throttle=" + _targetThrottle + " brake=" + _targetBrake); }

        public void EnableAutoShift(bool enabled) { _autoShiftEnabled = enabled; _rpmAboveThresholdSince = -1f; _rpmBelowThresholdSince = -1f; }

        private void UpdateAutoShift()
        {
            if (!_autoShiftEnabled || _drivetrain == null) return;
            float now = Time.time; float rpm = GetRpm(); int gear = GetGear();
            if (gear < GEAR_FIRST) return;

            if (gear < GEAR_LAST)
            {
                float thr = (gear == GEAR_FIRST) ? SHIFT_UP_RPM_FIRST : SHIFT_UP_RPM;
                bool timeOk = (gear != GEAR_FIRST) || (now - _firstGearEnteredAt > FIRST_GEAR_MIN_TIME);
                bool cdOk = now >= _nextShiftAllowedAt;
                if (rpm > thr && timeOk && cdOk)
                {
                    if (_rpmAboveThresholdSince < 0f) _rpmAboveThresholdSince = now;
                    if (now - _rpmAboveThresholdSince > RPM_STABLE_TIME)
                    {
                        ShiftTo(gear + 1);
                        _nextShiftAllowedAt = now + SHIFT_COOLDOWN;
                        _rpmAboveThresholdSince = -1f;
                    }
                }
                else _rpmAboveThresholdSince = -1f;
            }
            if (gear > GEAR_FIRST)
            {
                if (rpm < SHIFT_DOWN_RPM && now >= _nextShiftAllowedAt)
                {
                    if (_rpmBelowThresholdSince < 0f) _rpmBelowThresholdSince = now;
                    if (now - _rpmBelowThresholdSince > RPM_STABLE_TIME)
                    {
                        ShiftTo(gear - 1);
                        _nextShiftAllowedAt = now + SHIFT_COOLDOWN;
                        _rpmBelowThresholdSince = -1f;
                    }
                }
                else _rpmBelowThresholdSince = -1f;
            }
        }

        public bool StartMoveOff()
        {
            if (!inCar || currentCar == null || _driveSequenceRunning) return false;
            CacheCarComponents();
            if (_axis == null || _drivetrain == null) return false;
            StartCoroutine(MoveOffRoutine());
            return true;
        }

        private IEnumerator MoveOffRoutine()
        {
            _driveSequenceRunning = true;
            ShiftTo(GEAR_NEUTRAL);
            yield return new WaitForSeconds(0.3f);
            SetThrottle(1.0f);
            float t0 = Time.time;
            while (GetRpm() < 4000f &&
                   Time.time - t0 < 8f &&
                   !_navigationCancelRequested)
                yield return null;

            if (_navigationCancelRequested)
                yield break;
            ShiftTo(GEAR_FIRST);
            yield return new WaitForSeconds(0.3f);
            SetThrottle(0.25f);
            yield return new WaitForSeconds(1.0f);
            EnableAutoShift(true);
            SetThrottle(0.7f);
            float tDrive = Time.time;
            while (Time.time - tDrive < 15f) yield return null;
            EnableAutoShift(false);
            SetThrottle(0f);
            yield return new WaitForSeconds(0.5f);
            SetBrake(1.0f);
            yield return new WaitForSeconds(4f);
            ReleaseInput();
            ShiftTo(GEAR_NEUTRAL);
            _driveSequenceRunning = false;
        }

        // ═══════════════════════════════════════════════════════════
        //  ЗАПИСЬ МАРШРУТА
        // ═══════════════════════════════════════════════════════════

        private static string RoutesDir()
        {
            string modsDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(modsDir, "routes");
        }

        public void ToggleRecording()
        {
            if (_recording) StopRecording();
            else StartRecording();
        }

        private void StartRecording()
        {
            if (_awaitingRouteName) { Debug.Log("[V] Сначала введи имя предыдущего маршрута"); return; }
            var player = GameObject.Find("PLAYER");
            if (player == null) { Debug.Log("[V] ❌ PLAYER не найден"); return; }

            GameObject car = null;
            Collider[] cols = Physics.OverlapSphere(player.transform.position, 4f);
            float bestD = float.MaxValue;
            foreach (var c in cols)
            {
                if (c == null) continue;
                var rb = c.attachedRigidbody;
                if (rb == null || rb.mass < 100f) continue;
                float d = Vector3.Distance(player.transform.position, rb.transform.position);
                if (d < bestD) { bestD = d; car = rb.gameObject; }
            }

            if (car == null) { Debug.Log("[V] ❌ Машина не найдена (нужно быть в машине/рядом)"); return; }

            _recording = true;
            _recordingCar = car;
            _recordedPoints.Clear();
            _recordedPoints.Add(car.transform.position);
            Debug.Log("[V] ⏺️ НАЧАЛО ЗАПИСИ. Машина: " + car.name);
        }

        private void UpdateRecording()
        {
            if (!_recording) return;
            if (_recordingCar == null) { StopRecording(); return; }

            Vector3 pos = _recordingCar.transform.position;
            if (_recordedPoints.Count > 0)
            {
                Vector3 last = _recordedPoints[_recordedPoints.Count - 1];
                Vector3 a = new Vector3(pos.x, 0f, pos.z);
                Vector3 b = new Vector3(last.x, 0f, last.z);
                if (Vector3.Distance(a, b) < REC_MIN_DIST) return;
            }

            _recordedPoints.Add(pos);
            Debug.Log("[V] 📍 Точка " + _recordedPoints.Count);
        }

        private void StopRecording()
        {
            if (!_recording) return;
            _recording = false;
            _recordingCar = null;

            if (_recordedPoints.Count < 2)
            {
                Debug.Log("[V] ❌ Точек мало (" + _recordedPoints.Count + "), отмена");
                _recordedPoints.Clear();
                return;
            }

            _pendingRoutePoints.Clear();
            _pendingRoutePoints.AddRange(_recordedPoints);
            _recordedPoints.Clear();
            _awaitingRouteName = true;

            Debug.Log("[V] ⏹️ Запись остановлена. Точек: " + _pendingRoutePoints.Count + ". Введи имя маршрута (Esc — отмена)");
        }

        public void SaveRouteWithName(string name)
        {
            if (!_awaitingRouteName) return;
            if (string.IsNullOrEmpty(name) || name.Trim().Length == 0) return;

            name = name.Trim();
            foreach (char c in new char[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' })
                name = name.Replace(c, '_');

            try
            {
                string dir = RoutesDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string path = Path.Combine(dir, name + ".txt");
                var sb = new System.Text.StringBuilder();
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var p in _pendingRoutePoints)
                    sb.AppendLine(p.x.ToString("F2", ci) + "," + p.y.ToString("F2", ci) + "," + p.z.ToString("F2", ci));
                File.WriteAllText(path, sb.ToString());

                _lastSavedRoutePath = path;
                Debug.Log("[V] 💾 СОХРАНЕНО: " + path + " (" + _pendingRoutePoints.Count + " точек)");
            }
            catch (Exception e) { Debug.Log("[V] ❌ Ошибка сохранения: " + e.Message); }

            _pendingRoutePoints.Clear();
            _awaitingRouteName = false;
        }

        public void CancelSaveRoute()
        {
            if (!_awaitingRouteName) return;
            _pendingRoutePoints.Clear();
            _awaitingRouteName = false;
            Debug.Log("[V] 🗑️ Сохранение отменено");
        }

        // ═══════════════════════════════════════════════════════════
        //  ВОСПРОИЗВЕДЕНИЕ МАРШРУТА
        // ═══════════════════════════════════════════════════════════

        public void ToggleRoutePlayback()
        {
            if (_playingRoute || _playbackPreparing) { StopPlayback(); return; }
            if (!inCar || currentCar == null) { Debug.Log("[V] ❌ V должна быть в машине (F10)"); return; }

            LoadRouteFile();
            if (_playPoints.Count < 2) { Debug.Log("[V] ❌ Маршрут не найден или пустой"); return; }

            StartCoroutine(StartPlaybackRoutine());
        }

        private void LoadRouteFile()
        {
            _playPoints.Clear();

            string path = _lastSavedRoutePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                string dir = RoutesDir();
                if (!Directory.Exists(dir)) { Debug.Log("[V] ❌ Папка routes/ пуста"); return; }
                var files = Directory.GetFiles(dir, "*.txt");
                if (files.Length == 0) { Debug.Log("[V] ❌ Нет файлов в routes/"); return; }
                path = files[files.Length - 1];
                Debug.Log("[V] 📂 Загрузка маршрута: " + Path.GetFileName(path));
            }

            _currentRouteName = Path.GetFileNameWithoutExtension(path);

            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrEmpty(line) || line.Trim().Length == 0) continue;
                    var parts = line.Split(',');
                    if (parts.Length != 3) continue;
                    _playPoints.Add(new Vector3(
                        float.Parse(parts[0], ci),
                        float.Parse(parts[1], ci),
                        float.Parse(parts[2], ci)));
                }
                Debug.Log("[V] 📂 Точек: " + _playPoints.Count);
            }
            catch (Exception e) { Debug.Log("[V] ❌ Ошибка загрузки: " + e.Message); }
        }

        private IEnumerator StartPlaybackRoutine()
        {
            _playbackPreparing = true;
            Debug.Log("[V] ▶️ Подготовка машины...");

            SetThrottle(0f); SetBrake(1f);
            yield return new WaitForSeconds(0.5f);
            if (_navigationCancelRequested)
                yield break;
            if (!inCar || currentCar == null) { _playbackPreparing = false; yield break; }

            ShiftTo(GEAR_NEUTRAL);
            yield return new WaitForSeconds(0.4f);
            if (_navigationCancelRequested)
                yield break;
            if (!inCar || currentCar == null) { _playbackPreparing = false; yield break; }

            SetBrake(0f);
            SetThrottle(1.0f);
            float t0 = Time.time;
            while (GetRpm() < 4000f &&
                   Time.time - t0 < 8f &&
                   !_navigationCancelRequested)
                yield return null;

            if (_navigationCancelRequested)
                yield break;
            if (!inCar || currentCar == null) { _playbackPreparing = false; yield break; }

            ShiftTo(GEAR_FIRST);
            yield return new WaitForSeconds(0.2f);
            if (_navigationCancelRequested)
                yield break;
            if (!inCar || currentCar == null) { _playbackPreparing = false; yield break; }

            SetThrottle(0.25f);
            yield return new WaitForSeconds(0.8f);
            if (_navigationCancelRequested)
                yield break;
            if (!inCar || currentCar == null) { _playbackPreparing = false; yield break; }

            EnableAutoShift(true);
            SetThrottle(0.7f);

            _playIndex = 0;
            _playMinIndex = 0;
            _navT0 = Time.time;
            _navStartedAt = Time.time;
            _currentSteer = 0f;
            _playingRoute = true;
            _playbackPreparing = false;
            Debug.Log("[V] ▶️ ПОЕХАЛИ: " + _playPoints.Count + " точек");
        }

        private void UpdateRoutePlayback()
        {
            if (!_playingRoute ||
                _isFinishingRoute ||
                _navigationCancelRequested)
                return;

            if (currentCar == null)
            {
                StopPlayback();
                return;
            }

            Rigidbody rb = currentCar.GetComponent<Rigidbody>();
            float speedKmh = rb != null ? rb.velocity.magnitude * 3.6f : 0f;

            Vector3 carPos = currentCar.transform.position;
            Vector3 carFwd = currentCar.transform.forward;
            Vector3 carPosFlat = new Vector3(carPos.x, 0f, carPos.z);
            Vector3 fwdFlat = new Vector3(carFwd.x, 0f, carFwd.z).normalized;

            // 1. Если мы проехали все точки
            if (_playIndex >= _playPoints.Count - 1)
            {
                if (_navMultiSegment) { _segmentFinished = true; return; }

                if (!_isFinishingRoute)
                {
                    Debug.Log("[V] 🏁 Финиш. Плавно торможу...");
                    StartCoroutine(FinishRouteRoutine());
                }
                return;
            }

            // 2. Ищем ближайшую точку на пути, НО только впереди (чтобы не разворачивалась)
            int startIdx = _playIndex; // По умолчанию остаемся на текущем
            float minDist = float.MaxValue;

            // Ищем только на 5 точек назад и на 40 точек вперед
            int searchStart = Mathf.Max(_playMinIndex, _playIndex - 5);
            int searchEnd = Mathf.Min(_playPoints.Count - 1, _playIndex + 40);

            for (int i = searchStart; i <= searchEnd; i++)
            {
                Vector3 ptFlat = new Vector3(_playPoints[i].x, 0f, _playPoints[i].z);
                float d = Vector3.Distance(carPosFlat, ptFlat);
                if (d < minDist) { minDist = d; startIdx = i; }
            }

            _lastRouteDeviationMeters = minDist;

            // Синхронизируем индекс
            _playIndex = startIdx;

            // Если оказались на последней точке
            if (_playIndex >= _playPoints.Count - 1)
            {
                if (_navMultiSegment) { _segmentFinished = true; return; }
                if (!_isFinishingRoute) StartCoroutine(FinishRouteRoutine());
                return;
            }

            // ─── Определяем, где мы находимся ───
            bool isHighway = _currentRouteName.ToLower().Contains("highway");

            // ═══ СКЛЕЙКА: текущий сегмент от _playIndex + превью следующего ═══
            List<Vector3> effPts = new List<Vector3>();
            for (int i = _playIndex; i < _playPoints.Count; i++) effPts.Add(_playPoints[i]);
            for (int i = 0; i < _nextSegmentPreview.Count; i++) effPts.Add(_nextSegmentPreview[i]);

            // ─── Target (ДИНАМИЧЕСКИЙ look-ahead) ───
            float lookAheadDist = isHighway ? Mathf.Clamp(speedKmh * 0.35f, 25f, 50f) : 12f;
            int targetIdx = 0;
            float distAlongPath = 0f;
            Vector3 prevPt = carPos;

            for (int i = 0; i < effPts.Count; i++)
            {
                Vector3 pt = effPts[i];
                Vector3 ptFlat = new Vector3(pt.x, 0f, pt.z);
                Vector3 prevFlat = new Vector3(prevPt.x, 0f, prevPt.z);

                distAlongPath += Vector3.Distance(prevFlat, ptFlat);
                prevPt = pt;

                targetIdx = i;
                if (distAlongPath >= lookAheadDist) break;
            }

            Vector3 target = effPts[targetIdx];
            Vector3 toTarget = target - carPos; toTarget.y = 0f;
            float dist = toTarget.magnitude;

            toTarget.Normalize();
            float dot = Mathf.Clamp(Vector3.Dot(fwdFlat, toTarget), -1f, 1f);
            float angle = Mathf.Acos(dot) * Mathf.Rad2Deg;
            float sign = Mathf.Sign(Vector3.Cross(fwdFlat, toTarget).y);

            // ─── Кривизна (смотрим еще дальше на трассе) ───
            float curvature = 0f;
            float[] nearDist;
            if (isHighway) nearDist = new float[] { 40f, 70f, 100f };
            else nearDist = new float[] { 15f, 30f, 50f };

            foreach (float dNear in nearDist)
            {
                int i1 = 0, i2 = 0;
                float dist1 = 0f, dist2 = 0f;
                prevPt = carPos;

                for (int i = 0; i < effPts.Count; i++)
                {
                    Vector3 ptFlat = new Vector3(effPts[i].x, 0f, effPts[i].z);
                    Vector3 prevFlat = new Vector3(prevPt.x, 0f, prevPt.z);
                    dist1 += Vector3.Distance(prevFlat, ptFlat);
                    prevPt = effPts[i];
                    if (dist1 >= dNear) { i1 = i; break; }
                }

                if (i1 >= effPts.Count - 1) continue;

                prevPt = effPts[i1];
                dist2 = dist1;
                for (int i = i1; i < effPts.Count; i++)
                {
                    Vector3 ptFlat = new Vector3(effPts[i].x, 0f, effPts[i].z);
                    Vector3 prevFlat = new Vector3(prevPt.x, 0f, prevPt.z);
                    dist2 += Vector3.Distance(prevFlat, ptFlat);
                    prevPt = effPts[i];
                    if (dist2 >= dNear + 30f) { i2 = i; break; }
                }

                if (i1 < i2 && i2 < effPts.Count)
                {
                    Vector3 p1 = effPts[i1];
                    Vector3 p2 = effPts[i2];
                    Vector3 v1 = p1 - carPos; v1.y = 0f;
                    Vector3 v2 = p2 - p1; v2.y = 0f;
                    if (v1.magnitude > 0.5f && v2.magnitude > 0.5f)
                    {
                        float cd = Mathf.Clamp(Vector3.Dot(v1.normalized, v2.normalized), -1f, 1f);
                        float c = Mathf.Acos(cd) * Mathf.Rad2Deg;
                        if (c > curvature) curvature = c;
                    }
                }
            }

            // ─── Mid-look ───
            float midAngle = 0f;
            float heightDiff = 0f;
            int idxMid = 0;
            float distMid = 0f;
            float midLookDist = isHighway ? 80f : 30f;

            prevPt = carPos;
            for (int i = 0; i < effPts.Count; i++)
            {
                Vector3 ptFlat = new Vector3(effPts[i].x, 0f, effPts[i].z);
                Vector3 prevFlat = new Vector3(prevPt.x, 0f, prevPt.z);
                distMid += Vector3.Distance(prevFlat, ptFlat);
                prevPt = effPts[i];
                if (distMid >= midLookDist) { idxMid = i; break; }
            }

            if (idxMid < effPts.Count)
            {
                Vector3 toMid = effPts[idxMid] - carPos; toMid.y = 0f;
                toMid.Normalize();
                float mdot = Mathf.Clamp(Vector3.Dot(fwdFlat, toMid), -1f, 1f);
                midAngle = Mathf.Acos(mdot) * Mathf.Rad2Deg;
                heightDiff = carPos.y - effPts[idxMid].y;
            }

            // ─── Целевая скорость ───
            float maxSpeed = isHighway ? HighwayMaxSpeed : GravelMaxSpeed;
            float targetSpeed = maxSpeed;

            if (curvature > 45f || angle > 40f || midAngle > 25f) targetSpeed = maxSpeed * 0.35f;
            else if (curvature > 25f || angle > 25f || midAngle > 15f) targetSpeed = maxSpeed * 0.53f;
            else if (curvature > 12f || angle > 12f || midAngle > 8f) targetSpeed = maxSpeed * 0.70f;

            // ═══ ТОРМОЖЕНИЕ НА СПУСКЕ ═══
            if (heightDiff > _downhillThreshold)
            {
                targetSpeed = Mathf.Min(targetSpeed, maxSpeed * _downhillSpeedFactor);
            }

            // ═══ АНТИЦИПАЦИЯ КОНЦА МАРШРУТА (ПЛАВНОЕ ТОРМОЖЕНИЕ) ═══
            if (_nextSegmentPreview.Count == 0)
            {
                float distToEnd = 0f;
                Vector3 prevPtEnd = carPos;
                for (int i = 0; i < effPts.Count; i++)
                {
                    Vector3 ptFlat = new Vector3(effPts[i].x, 0f, effPts[i].z);
                    Vector3 prevFlat = new Vector3(prevPtEnd.x, 0f, prevPtEnd.z);
                    distToEnd += Vector3.Distance(prevFlat, ptFlat);
                    prevPtEnd = effPts[i];
                }

                if (distToEnd < 60f)
                {
                    float factor = distToEnd / 60f;
                    targetSpeed = Mathf.Min(targetSpeed, 8f + (maxSpeed * 0.5f * factor));
                }
            }

            if (Time.time - _navStartedAt < 3f) targetSpeed = Mathf.Min(targetSpeed, 25f);

            // ─── Газ / тормоз ───
            float throttle = 0f, brake = 0f;
            float diff = speedKmh - targetSpeed;
            if (diff > 8f) { brake = 0.45f; throttle = 0f; }
            else if (diff > 3f) { brake = 0.30f; throttle = 0f; }
            else if (diff > -5f) { brake = 0f; throttle = 0.25f; }
            else if (diff > -15f) { brake = 0f; throttle = 0.6f; }
            else { brake = 0f; throttle = 0.9f; }

            if (speedKmh < 5f && throttle < 0.3f) throttle = 0.5f;

            // ─── Руль ───
            float rawSteer = sign * Mathf.Clamp01(angle / 25f);
            float steerLerpSpeed = 6f;
            if (isHighway && speedKmh > 70f)
            {
                steerLerpSpeed = Mathf.Lerp(6f, 2.5f, Mathf.Clamp01((speedKmh - 70f) / 60f));
            }

            _currentSteer = Mathf.Lerp(_currentSteer, rawSteer, Time.deltaTime * steerLerpSpeed);

            SetSteer(_currentSteer);
            SetThrottle(throttle);
            SetBrake(brake);

            if (Time.frameCount % 30 == 0)
            {
                Debug.Log("[V] 🚗 " + _playIndex + "/" + _playPoints.Count +
                          " dist=" + dist.ToString("F0") +
                          " angle=" + angle.ToString("F0") + "°" +
                          " curv=" + curvature.ToString("F0") + "°" +
                          " sp=" + speedKmh.ToString("F0") + "/" + targetSpeed.ToString("F0"));
            }
        }

        private IEnumerator FinishRouteRoutine(bool requestedCancel = false)
        {
            if (requestedCancel)
                _navigationCancelRequested = true;

            if (_isFinishingRoute)
                yield break;

            _isFinishingRoute = true;

            _navigationOutcome = _navigationCancelRequested
                ? "stopping"
                : "finishing";

            Debug.Log("[V] 🛑 Начинаю плавное торможение...");

            SetThrottle(0f);
            SetSteer(0f);
            SetBrake(1f);

            float timeout = 10f;

            Rigidbody rb = currentCar != null
                ? currentCar.GetComponent<Rigidbody>()
                : null;

            while (
                timeout > 0f &&
                (_playingRoute ||
                 _playbackPreparing ||
                 _navigationCancelRequested)
            )
            {
                float speedKmh = rb != null
                    ? rb.velocity.magnitude * 3.6f
                    : 0f;

                if (speedKmh < 2f)
                    break;

                timeout -= Time.deltaTime;
                yield return null;
            }

            if (!_playingRoute &&
                !_playbackPreparing &&
                !_navigationCancelRequested)
            {
                yield break;
            }

            float finalSpeed = rb != null
                ? rb.velocity.magnitude * 3.6f
                : 0f;

            if (finalSpeed >= 2f)
            {
                Debug.LogWarning(
                    "[V] ❌ Не удалось полностью остановить машину."
                );

                _navigationOutcome = "failed";

                // Оставляем тормоз нажатым даже при ошибке.
                SetThrottle(0f);
                SetSteer(0f);
                SetBrake(1f);

                ShiftTo(GEAR_NEUTRAL);
                EnableAutoShift(false);

                _playingRoute = false;
                _playbackPreparing = false;
                _navMultiSegment = false;
                _segmentFinished = false;
                _playMinIndex = 0;
                _nextSegmentPreview.Clear();

                _isFinishingRoute = false;
                _navigationCancelRequested = false;

                if (GraphVisualizer.Instance != null)
                    GraphVisualizer.Instance.ClearHighlight();

                yield break;
            }

            Debug.Log("[V] ✅ Машина остановлена. Нейтралка.");

            ShiftTo(GEAR_NEUTRAL);
            ReleaseInput();

            yield return new WaitForSeconds(1f);

            if (!_playingRoute &&
                !_playbackPreparing &&
                !_navigationCancelRequested)
            {
                yield break;
            }

            bool wasCancelled = _navigationCancelRequested;

            if (wasCancelled)
            {
                Debug.Log("[V] ⏹️ Навигация отменена после остановки.");
            }
            else
            {
                Debug.Log("[V] 🏁 Навигация успешно завершена.");
            }

            StopPlayback(!wasCancelled);
            _navigationOutcome = wasCancelled
                ? "cancelled"
                : "succeeded";

            _navigationCancelRequested = false;
        }

        private void StopPlayback()
        {
            StopPlayback(false);
        }

        private void StopPlayback(bool completed)
        {
            if (!_playingRoute &&
                !_playbackPreparing &&
                !_isFinishingRoute)
                return;

            // Сразу фиксируем терминальный результат маршрута.
            _navigationOutcome = completed
                ? "succeeded"
                : "cancelled";

            // Все флаги активности сбрасываются безусловно.
            // Старый код сначала менял _navigationOutcome на "succeeded",
            // а затем проверял, равен ли он "running"/"finishing".
            // Из-за этого _playingRoute оставался true после успешного финиша.
            _playingRoute = false;
            _playbackPreparing = false;
            _navMultiSegment = false;
            _segmentFinished = false;
            _playMinIndex = 0;
            _nextSegmentPreview.Clear();
            _isFinishingRoute = false;
            _navigationCancelRequested = false;

            EnableAutoShift(false);
            ReleaseInput();

            Debug.Log("[V] ⏹️ Остановлена");

            if (GraphVisualizer.Instance != null)
                GraphVisualizer.Instance.ClearHighlight();
        }

        public bool CancelNavigation()
        {
            if (!IsNavigationActive)
                return false;

            if (_navigationCancelRequested)
                return true;

            _navigationCancelRequested = true;
            _navigationOutcome = "stopping";

            if (!_isFinishingRoute)
                StartCoroutine(FinishRouteRoutine(true));

            return true;
        }

        // ═══════════════════════════════════════════════════════════
        //  ГРАФ МАРШРУТОВ
        // ═══════════════════════════════════════════════════════════

        public void BuildRouteGraph()
        {
            string dir = RoutesDir();
            Debug.Log("[V][GRAPH] Сканирование: " + dir);
            _graph.LoadAll(dir);
            _graph.BuildGraph();
            _navigator = new RouteNavigator(_graph);
            Debug.Log(_graph.Dump());

            try
            {
                string path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    "V_graph.txt");
                System.IO.File.WriteAllText(path, _graph.Dump());
            }
            catch (Exception e) { Debug.Log("[V][GRAPH] Ошибка записи: " + e.Message); }

            // ═══ ВИЗУАЛИЗАЦИЯ ═══
            if (GraphVisualizer.Instance == null)
            {
                var go = new GameObject("V_GraphVisualizer");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<GraphVisualizer>();
            }
            GraphVisualizer.Instance.BuildFrom(_graph);
            Debug.Log("[V][GRAPH] ✅ Визуализация готова. Ctrl+G - переключение режимов");
        }

        private void EnsureNavigator()
        {
            if (_navigator != null) return;
            if (_graph.Routes.Count == 0)
            {
                _graph.LoadAll(RoutesDir());
                _graph.BuildGraph();
            }
            _navigator = new RouteNavigator(_graph);
        }

        public List<string> GetAllDestinations()
        {
            EnsureNavigator();
            return _navigator.GetAllDestinations();
        }

        public bool StartNavigationTo(string dest)
        {
            if (!inCar || currentCar == null) { Debug.Log("[V] ❌ Не в машине"); return false; }
            if (_playingRoute ||
            _playbackPreparing ||
            _isFinishingRoute) { Debug.Log("[V] ❌ Уже едем"); return false; }

            EnsureNavigator();

            int goal = _navigator.FindDestinationNode(dest);
            if (goal < 0) { Debug.Log("[V] ❌ Destination '" + dest + "' не найден в графе"); return false; }

            int startNodeId;
            var path = _navigator.GetPathFromPosition(currentCar.transform.position, currentCar.transform.forward, goal, out startNodeId);

            if (path == null || path.Count == 0)
            {
                Debug.Log("[V] ❌ Не удалось найти путь от текущей позиции");
                return false;
            }

            if (startNodeId < 0)
            {
                Debug.Log("[V] ❌ Машина не находится ни на одном маршруте");
                return false;
            }

            Debug.Log("[V] 📍 Путь от N" + startNodeId + " в N" + goal + ": " + path.Count + " сегментов");
            foreach (var s in path) Debug.Log("[V]    " + s);

            // Все переходы между сегментами рассчитываем заранее, пока машина стоит.
            var transitionPlanner = new RouteTransitionPlanner();
            List<RouteTransitionPlanner.TransitionPlan> transitionPlans =
                transitionPlanner.BuildPlans(path);

            _navigationCancelRequested = false;
            _navigationDestination = dest;
            _navigationOutcome = "running";
            _lastRouteDeviationMeters = -1f;

            _navMultiSegment = true;
            StartCoroutine(NavigateRoutine(path, transitionPlans, dest));
            return true;
        }

        private IEnumerator NavigateRoutine(
            List<RouteNavigator.NavStep> path,
            List<RouteTransitionPlanner.TransitionPlan> transitionPlans,
            string dest)
        {
            _playbackPreparing = true;
            Debug.Log("[V] ▶️ Навигация: подготовка");

            SetThrottle(0f); SetBrake(1f);
            yield return new WaitForSeconds(0.5f);
            if (!inCar || currentCar == null) { _playbackPreparing = false; _navMultiSegment = false; yield break; }

            ShiftTo(GEAR_NEUTRAL);
            yield return new WaitForSeconds(0.4f);
            if (!inCar || currentCar == null) { _playbackPreparing = false; _navMultiSegment = false; yield break; }

            SetBrake(0f); SetThrottle(1.0f);
            float t0 = Time.time;
            while (GetRpm() < 4000f &&
                   Time.time - t0 < 8f &&
                   !_navigationCancelRequested)
                yield return null;

            if (_navigationCancelRequested)
                yield break;
            if (!inCar || currentCar == null) { _playbackPreparing = false; _navMultiSegment = false; yield break; }

            ShiftTo(GEAR_FIRST);
            yield return new WaitForSeconds(0.2f);
            if (!inCar || currentCar == null) { _playbackPreparing = false; _navMultiSegment = false; yield break; }

            SetThrottle(0.25f);
            yield return new WaitForSeconds(0.8f);
            if (!inCar || currentCar == null) { _playbackPreparing = false; _navMultiSegment = false; yield break; }

            EnableAutoShift(true);
            SetThrottle(0.7f);

            _navStartedAt = Time.time;
            _playingRoute = true;
            _playbackPreparing = false;

            for (int si = 0; si < path.Count; si++)
            {
                var step = path[si];
                _currentRouteName = step.Edge.Route.Name; // Запоминаем, по какой дороге едем
                _playPoints.Clear();
                var pts = step.Edge.Route.Points;

                // Разворачиваем точки, если едем против направления ребра
                if (step.FromNode == step.Edge.FromNode)
                    _playPoints.AddRange(pts);
                else
                    for (int i = pts.Count - 1; i >= 0; i--) _playPoints.Add(pts[i]);

                // ═══ ПОДГОТОВКА ПЕРЕХОДА НА СЛЕДУЮЩИЙ СЕГМЕНТ ═══
                _nextSegmentPreview.Clear();

                // Подсветка текущего сегмента.
                if (GraphVisualizer.Instance != null)
                {
                    bool rev = (step.FromNode != step.Edge.FromNode);
                    GraphVisualizer.Instance.HighlightEdge(step.Edge, rev);
                }

                // Первый сегмент начинается с позиции машины.
                // Каждый последующий — с точки, заранее выбранной планировщиком.
                int startIdx = 0;
                if (si == 0)
                {
                    // StartPointIndex уже задан в направлении движения.
                    // Не переворачиваем его повторно для обратного направления.
                    startIdx = Mathf.Clamp(step.StartPointIndex, 0, _playPoints.Count - 1);
                }
                else if (transitionPlans != null && si - 1 < transitionPlans.Count)
                {
                    startIdx = Mathf.Clamp(
                        transitionPlans[si - 1].NextStartPointIndex, 0, _playPoints.Count - 1);
                }

                _playMinIndex = startIdx;

                // В конец текущего сегмента добавляем заранее рассчитанную
                // плавную кривую до выбранной точки следующего сегмента.
                if (si < path.Count - 1 && transitionPlans != null && si < transitionPlans.Count)
                {
                    var transition = transitionPlans[si];
                    if (transition != null && transition.ConnectorPoints != null)
                    {
                        // Connector samples must not duplicate the last current-segment
                        // point in the XZ plane. Playback also measures nearest-point
                        // distance in XZ, so a vertical-only duplicate would stall it too.
                        for (int cp = 0; cp < transition.ConnectorPoints.Count; cp++)
                        {
                            Vector3 connectorPoint = transition.ConnectorPoints[cp];
                            if (_playPoints.Count > 0)
                            {
                                Vector3 previousPoint = _playPoints[_playPoints.Count - 1];
                                previousPoint.y = 0f;
                                Vector3 flatConnectorPoint = connectorPoint;
                                flatConnectorPoint.y = 0f;
                                if (Vector3.Distance(previousPoint, flatConnectorPoint) < 0.1f)
                                    continue;
                            }
                            _playPoints.Add(connectorPoint);
                        }
                    }

                    // После точки подключения показываем контроллеру продолжение
                    // следующей дороги, чтобы существующий look-ahead видел её направление.
                    var nextStep = path[si + 1];
                    var nextPts = nextStep.Edge.Route.Points;
                    bool nextForward = (nextStep.FromNode == nextStep.Edge.FromNode);
                    int nextStart = Mathf.Clamp(transition.NextStartPointIndex, 0, nextPts.Count - 1);
                    int previewCount = Mathf.Min(25, nextPts.Count - nextStart - 1);
                    for (int k = 1; k <= previewCount; k++)
                    {
                        int idx = nextForward ? nextStart + k : nextPts.Count - 1 - (nextStart + k);
                        if (idx >= 0 && idx < nextPts.Count)
                            _nextSegmentPreview.Add(nextPts[idx]);
                    }
                }

                _playIndex = startIdx;
                _navT0 = Time.time;
                _segmentFinished = false;

                Debug.Log("[V] ▶️ Сегмент " + (si + 1) + "/" + path.Count + ": " + step.Edge.Route.Name +
                          " (" + _playPoints.Count + " точек, старт с " + _playIndex +
                          ", min=" + _playMinIndex + ", preview=" + _nextSegmentPreview.Count + ")");

                while (!_segmentFinished &&
                       _playingRoute &&
                       !_navigationCancelRequested)
                    yield return null;

                if (_navigationCancelRequested)
                    yield break;

                if (!_playingRoute)
                    break;
            }
            _nextSegmentPreview.Clear();

            if (_playingRoute)
            {
                Debug.Log("[V] 🏁 Доехали до: " + dest);
                StartCoroutine(FinishRouteRoutine());
            }
            _navMultiSegment = false;
        }

        // ─── контекст ───

        public List<string> GetNearbyContext(float radius = 15f, int maxItems = 10)
        {
            var result = new List<string>();
            Vector3 center = transform.position + Vector3.up * 0.9f;
            Collider[] cols = Physics.OverlapSphere(center, radius);
            _seenRoots.Clear();
            var cand = new List<KeyValuePair<float, string>>(cols.Length);
            for (int i = 0; i < cols.Length; i++)
            {
                var col = cols[i]; if (col == null) continue;
                GameObject obj = col.gameObject; if (obj == gameObject) continue;
                var rbo = obj.GetComponentInParent<Rigidbody>();
                if (rbo == null) continue;
                GameObject root = rbo.gameObject;
                if (_seenRoots.Contains(root)) continue;
                _seenRoots.Add(root);
                if (root.name == "PLAYER" || root == gameObject) continue;
                float dist = Vector3.Distance(center, col.transform.position);
                if (dist > radius) continue;
                string lbl = CleanName(root.name);
                if (string.IsNullOrEmpty(lbl)) continue;
                cand.Add(new KeyValuePair<float, string>(dist, lbl + " (" + (int)dist + "м)"));
            }
            cand.Sort((a, b) => a.Key.CompareTo(b.Key));
            int lim = cand.Count < maxItems ? cand.Count : maxItems;
            for (int i = 0; i < lim; i++) result.Add(cand[i].Value);
            return result;
        }

        public string GetNearbyObjectsJson(
            float radius = 15f,
            int maxItems = 100)
        {
            var candidates = new List<KeyValuePair<float, Rigidbody>>();
            var seenIds = new HashSet<int>();
            PruneObjectRegistry();

            Vector3 center = transform.position + Vector3.up * 0.9f;

            if (radius < 1f) radius = 1f;
            if (radius > 50f) radius = 50f;

            if (maxItems < 1) maxItems = 1;
            if (maxItems > 200) maxItems = 200;

            Collider[] cols = Physics.OverlapSphere(center, radius);

            // Собираем уникальные физические объекты.
            for (int i = 0; i < cols.Length; i++)
            {
                Collider col = cols[i];

                if (col == null) continue;

                Rigidbody rb = col.GetComponentInParent<Rigidbody>();

                if (rb == null) continue;

                GameObject root = rb.gameObject;

                if (root == null ||
                    root == gameObject ||
                    root.name == "PLAYER")
                    continue;

                int instanceId = root.GetInstanceID();

                // Один Rigidbody может иметь несколько Collider.
                if (!seenIds.Add(instanceId))
                    continue;

                float distance = Vector3.Distance(
                    center,
                    root.transform.position
                );

                if (distance > radius)
                    continue;

                // Сохраняем ссылку на конкретный объект Unity.
                _objectRegistry[instanceId] = rb;

                candidates.Add(
                    new KeyValuePair<float, Rigidbody>(
                        distance,
                        rb
                    )
                );
            }

            // Сначала ближайшие предметы.
            candidates.Sort(
                (a, b) => a.Key.CompareTo(b.Key)
            );

            var groupedObjects =
                new Dictionary<string, List<string>>(
                    StringComparer.OrdinalIgnoreCase
                );

            var groupOrder = new List<string>();

            int count = Mathf.Min(maxItems, candidates.Count);

            for (int i = 0; i < count; i++)
            {
                Rigidbody rb = candidates[i].Value;

                if (rb == null)
                    continue;

                GameObject root = rb.gameObject;

                // Для поиска предметов показываем только то,
                // что V действительно может поднять.
                if (rb.mass >= 100f)
                    continue;

                string objectName = CleanName(root.name);

                if (string.IsNullOrEmpty(objectName))
                    continue;

                if (!groupedObjects.ContainsKey(objectName))
                {
                    groupedObjects[objectName] = new List<string>();
                    groupOrder.Add(objectName);
                }

                string id = "object_" + root.GetInstanceID();

                string itemJson =
                    "{"
                    + "\"id\":\"" + EscapeNearbyJson(id) + "\","
                    + "\"distance\":" + NearbyFloat(candidates[i].Key)
                    + "}";

                groupedObjects[objectName].Add(itemJson);
            }

            var jsonGroups = new List<string>();

            foreach (string name in groupOrder)
            {
                string groupJson =
                    "\""
                    + EscapeNearbyJson(name)
                    + "\":["
                    + string.Join(
                        ",",
                        groupedObjects[name].ToArray()
                    )
                    + "]";

                jsonGroups.Add(groupJson);
            }

            return "{"
                + string.Join(",", jsonGroups.ToArray())
                + "}";
        }

        private void PruneObjectRegistry()
        {
            var staleIds = new List<int>();

            foreach (var entry in _objectRegistry)
            {
                if (entry.Value == null)
                    staleIds.Add(entry.Key);
            }

            foreach (int id in staleIds)
            {
                _objectRegistry.Remove(id);
            }
        }

        private bool TryGetRegisteredObject(
            string objectId,
            out Rigidbody rb)
        {
            rb = null;

            const string prefix = "object_";

            if (string.IsNullOrEmpty(objectId) ||
                !objectId.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            int instanceId;

            if (!int.TryParse(
                objectId.Substring(prefix.Length),
                out instanceId))
                return false;

            if (!_objectRegistry.TryGetValue(instanceId, out rb) ||
                rb == null)
            {
                _objectRegistry.Remove(instanceId);
                rb = null;
                return false;
            }

            return true;
        }

        private static string EscapeNearbyJson(string value)
        {
            if (value == null) return "";

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        private static string NearbyFloat(float value)
        {
            return value.ToString(
                "R",
                System.Globalization.CultureInfo.InvariantCulture
            );
        }

        private static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int p = name.IndexOf('('); if (p > 0) name = name.Substring(0, p).Trim();
            if (name.Length > 25) name = name.Substring(0, 25);
            return name;
        }

        public GameObject FindNearestCar(float radius = 8f)
        {
            Collider[] cols = Physics.OverlapSphere(transform.position, radius);
            GameObject best = null; float bestDist = float.MaxValue;
            foreach (var c in cols)
            {
                if (c == null) continue;
                Rigidbody rb = c.attachedRigidbody;
                if (rb == null || rb.mass < 100f) continue;
                float d = Vector3.Distance(transform.position, rb.transform.position);
                if (d < bestDist) { bestDist = d; best = rb.gameObject; }
            }
            if (best != null)
            {
                Rigidbody bestRb = best.GetComponent<Rigidbody>();

                Debug.Log(
                    "[V][CAR SEARCH] selected=" + best.name +
                    ", root=" + best.transform.root.name +
                    ", mass=" +
                    (bestRb != null ? bestRb.mass.ToString("F1") : "no RB") +
                    ", transforms=" +
                    best.GetComponentsInChildren<Transform>().Length
                );
            }
            else
            {
                Debug.LogWarning("[V][CAR SEARCH] Машина не найдена.");
            }

            return best;
        }

        private Transform FindNearestCarSeat(GameObject car)
        {
            Transform bestSeat = null;
            float bestDist = float.MaxValue;
            Vector3 vPos = transform.position;

            foreach (Transform t in car.GetComponentsInChildren<Transform>())
            {
                string n = t.name.ToLower();
                // Ищем все, что похоже на сиденье
                if (n.Contains("seat") || n.Contains("driver") || n.Contains("passenger") || n.Contains("drive"))
                {
                    float d = Vector3.Distance(vPos, t.position);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestSeat = t;
                    }
                }
            }
            return bestSeat;
        }

        // Поиск конкретно водительского сиденья
        private Transform FindDriverSeat(GameObject car)
        {
            foreach (Transform t in car.GetComponentsInChildren<Transform>())
            {
                string n = t.name.ToLower();
                if (n.Contains("door") || n.Contains("window") || n.Contains("wheel")) continue;
                if (n.Contains("driver") || n.Contains("drive")) return t;
            }
            return FindNearestCarSeat(car); // если не нашли по имени — берем ближайшее
        }

        // Поиск конкретно пассажирского сиденья
        private Transform FindPassengerSeat(GameObject car)
        {
            foreach (Transform t in car.GetComponentsInChildren<Transform>())
            {
                string n = t.name.ToLower();
                if (n.Contains("door") || n.Contains("window") || n.Contains("wheel")) continue;
                if (n.Contains("passenger")) return t;
            }
            // Если пассажирского нет, ищем любое кресло, которое не является водительским
            foreach (Transform t in car.GetComponentsInChildren<Transform>())
            {
                string n = t.name.ToLower();
                if (n.Contains("door") || n.Contains("window") || n.Contains("wheel")) continue;
                if (n.Contains("seat") && !n.Contains("driver") && !n.Contains("drive")) return t;
            }
            return null;
        }


        // ═══ НАСТРОЙКИ СКОРОСТИ ═══
        public static float GravelMaxSpeed = 85f;   // Твоя базовая скорость на гравийке
        public static float HighwayMaxSpeed = 120f; // Максимальная на трассе
        private string _currentRouteName = "";

        // ═══ НАСТРОЙКИ СПУСКА ═══
        private float _downhillThreshold = 3.0f;  // Перепад высоты в метрах, при котором начинаем тормозить
        private float _downhillSpeedFactor = 0.7f; // До какой скорости сбрасывать (0.4 = 40% от максимальной)

        // ОБЩИЙ МЕТОД ПОСАДКИ (используется двумя кнопками)
        // ═══ НАСТРОЙКИ СИДЕНИЙ (Меняются через ползунки в игре) ═══
        public static Vector3 DriverSeatOffset = new Vector3(0.05f, -1.21f, -0.15f);
        public static Vector3 PassengerSeatOffset = new Vector3(-0.05f, -0.56f, -0.14f);

        // ═══ НАСТРОЙКИ МАСШТАБА ПРИ ПОСАДКЕ ═══
        public static float DriverSeatScale = 0.81f;
        public static float PassengerSeatScale = 0.81f;

        // НОВЫЕ ПЕРЕМЕННЫЕ ДЛЯ ИГРОКА
        public static Vector3 PlayerPassengerOffset = new Vector3(0f, 0.17f, -0.12f);
        public static Vector3 PlayerPassengerRotation = new Vector3(0f, 0f, 0f);

        private bool _playerSitting = false;
        private Vector3 _playerOriginalPos;
        private Quaternion _playerOriginalRot;
        private Transform _playerTransform;
        private GameObject _playerCar;
        private Transform _playerSeat;

        private Quaternion _lastCarRot;

        private bool _isPassengerSeat = false;
        private Vector3 _seatLocalPos;

        // ... остальной твой код ...

        // ═══ НАСТРОЙКИ ПОДХОДА К МАШИНЕ ═══
        private float _approachSideOffset = 1.0f;  // Насколько далеко в сторону от двери она встанет (1.0 = 1 метр)
        private float _approachRearOffset = 1.0f;  // Насколько далеко НАЗАД от двери она встанет (1.0 = 1 метр)
        private float _detourDistance = 2.5f;      // Радиус обхода машины (2.5 = 2.5 метра от центра)

        private IEnumerator EnterCarSmoothRoutine(bool isPassenger)
        {
            _isEnteringCar = true;
            _isPassengerSeat = isPassenger;

            carSeat = isPassenger ? FindPassengerSeat(currentCar) : FindDriverSeat(currentCar);
            GameObject door = FindDoorNearSeat(currentCar, carSeat);
            Debug.Log(
                "[V][ENTRY DEBUG] mode=" +
                (isPassenger ? "passenger" : "driver") +
                ", car=" +
                (currentCar != null ? currentCar.name : "<null>") +
                ", seat=" +
                (carSeat != null ? carSeat.name : "<null>") +
                ", door=" +
                (door != null ? door.name : "<null>")
            );

            if (carSeat == null && currentCar != null)
            {
                foreach (Transform t in
                    currentCar.GetComponentsInChildren<Transform>())
                {
                    Debug.Log("[V][ENTRY DEBUG] child=" + t.name);
                }
            }

            if (carSeat != null && door != null)
            {
                // 1. Вычисляем, с какой стороны дверь (1 = правая, -1 = левая)
                float doorLocalX = currentCar.transform.InverseTransformPoint(door.transform.position).x;
                float side = Mathf.Sign(doorLocalX);

                // 2. Вычисляем точку остановки у двери
                // Берем позицию двери, отступаем в сторону (вправо/влево) и СМЕЩАЕМ НАЗАД (против вектора forward)
                Vector3 doorTarget = door.transform.position
                                     + (currentCar.transform.right * side * _approachSideOffset)
                                     - (currentCar.transform.forward * _approachRearOffset);

                // 3. Смотрим, где находимся мы
                float vLocalX = currentCar.transform.InverseTransformPoint(transform.position).x;
                float vSide = Mathf.Sign(vLocalX);

                // 4. Если мы на противоположной стороне — строим точку обхода (сзади машины)
                if (vSide != side)
                {
                    // Точка обхода: сбоку от машины + сзади
                    Vector3 waypoint = currentCar.transform.position
                                       + (currentCar.transform.right * side * _detourDistance)
                                       - (currentCar.transform.forward * _detourDistance);

                    Debug.Log("[V] Обхожу машину к нужной двери...");
                    yield return StartCoroutine(MoveToPoint(waypoint));
                }

                // 5. Идем к двери
                Debug.Log("[V] Подхожу к двери...");
                yield return StartCoroutine(MoveToPoint(doorTarget));

                // 6. Открываем дверь
                MoveDoorPhysical(door, true);
                yield return new WaitForSeconds(1.2f); // Ждем, пока дверь откроется

                // 7. Садимся
                EnterCarBase(carSeat, isPassenger);
                yield return new WaitForSeconds(0.3f);

                // 8. Закрываем дверь
                MoveDoorPhysical(door, false);
            }
            else
            {
                EnterCarBase(carSeat, isPassenger);
            }

            _isEnteringCar = false;
        }

        private IEnumerator MoveToPoint(Vector3 targetPos)
        {
            _isWalkingToPoint = true;   // <--- НОВОЕ
            float timeout = 10f;

            while (Vector3.Distance(transform.position, targetPos) > 1.2f && timeout > 0)
            {
                Vector3 dir = targetPos - transform.position;
                dir.y = 0f;

                if (dir.magnitude > 0.1f)
                {
                    Quaternion look = Quaternion.LookRotation(dir);
                    transform.rotation = Quaternion.Slerp(transform.rotation, look, Time.deltaTime * 8f);

                    Vector3 move = transform.forward * WalkSpeed;
                    move.y = -9.81f;

                    if (cc != null && cc.enabled)
                        cc.Move(move * Time.deltaTime);
                    else
                        transform.position += move * Time.deltaTime;
                }

                timeout -= Time.deltaTime;
                yield return null;
            }

            _isWalkingToPoint = false;  // <--- НОВОЕ
        }

        private GameObject FindDoorNearSeat(GameObject car, Transform seat)
        {
            if (seat == null) return null;

            GameObject bestDoor = null;
            float minDist = float.MaxValue;

            foreach (Transform t in car.GetComponentsInChildren<Transform>())
            {
                string n = t.name.ToLower();
                // Ищем двери, у которых есть петли
                if (n.Contains("door") && t.GetComponent<HingeJoint>() != null)
                {
                    float d = Vector3.Distance(seat.position, t.position);
                    if (d < minDist)
                    {
                        minDist = d;
                        bestDoor = t.gameObject;
                    }
                }
            }
            return bestDoor;
        }

        void LateUpdate()
        {
            if (inCar && !_isPassengerSeat && currentCar != null)
            {
                // ═══ НАСТРОЙКИ ═══
                // Коэффициент для 1.5 оборотов руля. 
                // Если рука все равно не доходит до 3 часов, увеличь до 1.6f или 1.8f.
                float steeringSensitivity = 1.5f;

                Vector3 topArmOffset = new Vector3(0f, 0f, -0.1f);
                Vector3 bottomArmOffset = new Vector3(0f, 0f, 0.1f);
                // ══════════════════════════════

                float steerValue = 0f;
                if (_axis != null)
                {
                    object steerField = GetField(_axis, "steering");
                    if (steerField == null) steerField = GetField(_axis, "steerInput");
                    if (steerField == null) steerField = GetField(_axis, "steer");
                    if (steerField is float) steerValue = (float)steerField;
                }
                if (_injectInput) steerValue = _targetSteer;

                // УБРАЛИ Clamp(steerValue, -1, 1), так как руль ходит до 1.5

                // ═══ БАЗОВЫЕ ПОЗЫ ═══
                Quaternion q9_Clav = Quaternion.Euler(293.54f, 292.01f, 73.81f);
                Quaternion q9_Up = Quaternion.Euler(350.97f, 226.18f, 219.26f);
                Quaternion q9_Fore = Quaternion.Euler(331.41f, 4.54f, 346.70f);
                Quaternion q9_Hand = Quaternion.Euler(21.64f, 290.30f, 342.93f);

                Quaternion q9_12_Up = Quaternion.Euler(369.97f, 242.79f, -159.34f);
                Quaternion q9_12_Fore = Quaternion.Euler(-16.98f, -2.38f, 2.18f);
                Quaternion q9_12_Hand = Quaternion.Euler(40.39f, 213.92f, 333.39f);

                Quaternion q12_Up = Quaternion.Euler(387.63f, 246.75f, -167.33f);
                Quaternion q12_Fore = Quaternion.Euler(-1.46f, -1.80f, 2.09f);
                Quaternion q12_Hand = Quaternion.Euler(35.66f, 216.81f, 335.17f);

                Quaternion q12_3_Up = Quaternion.Euler(394.37f, 224.11f, -186.19f);
                Quaternion q12_3_Fore = Quaternion.Euler(-6.06f, -0.45f, -6.33f);
                Quaternion q12_3_Hand = Quaternion.Euler(29.26f, 176.98f, 361.15f);

                Quaternion q12_Clav = Quaternion.Euler(317.00f, 96.99f, 264.33f);

                Quaternion q9_6_Up = Quaternion.Euler(329.73f, 212.25f, 227.20f);
                Quaternion q9_6_Fore = Quaternion.Euler(-80.98f, -7.03f, -30.50f);
                Quaternion q9_6_Hand = Quaternion.Euler(34.47f, 217.70f, 360.01f);

                Quaternion q6_Up = Quaternion.Euler(307.74f, 177.51f, 211.32f);
                Quaternion q6_Fore = Quaternion.Euler(316.70f, 65.39f, 256.18f);
                Quaternion q6_Hand = Quaternion.Euler(14.77f, 134.17f, 341.14f);

                Quaternion q6_3_Up = Quaternion.Euler(331.22f, 212.76f, 170.51f);
                Quaternion q6_3_Fore = Quaternion.Euler(-71.77f, -26.64f, -0.37f);
                Quaternion q6_3_Hand = Quaternion.Euler(22.73f, 198.16f, 347.52f);

                Quaternion q6_Clav = Quaternion.Euler(293.55f, 292.01f, 73.82f);

                Quaternion q3_Up = Quaternion.Euler(357.50f, 234.56f, 200.53f);
                Quaternion q3_Fore = Quaternion.Euler(325.81f, 359.44f, 357.48f);
                Quaternion q3_Hand = Quaternion.Euler(10.97f, 196.97f, 9.63f);

                Quaternion q3_Clav = Quaternion.Euler(310.85f, 95.18f, 280.53f);

                // ═══ МАССИВЫ КЛЮЧЕВЫХ ТОЧЕК ═══
                Quaternion[] upArray, foreArray, handArray;

                if (steerValue >= 0) // ВПРАВО
                {
                    upArray = new Quaternion[] { q9_Up, q9_12_Up, q12_Up, q12_3_Up, q3_Up };
                    foreArray = new Quaternion[] { q9_Fore, q9_12_Fore, q12_Fore, q12_3_Fore, q3_Fore };
                    handArray = new Quaternion[] { q9_Hand, q9_12_Hand, q12_Hand, q12_3_Hand, q3_Hand };
                }
                else // ВЛЕВО
                {
                    upArray = new Quaternion[] { q9_Up, q9_6_Up, q6_Up, q6_3_Up, q3_Up };
                    foreArray = new Quaternion[] { q9_Fore, q9_6_Fore, q6_Fore, q6_3_Fore, q3_Fore };
                    handArray = new Quaternion[] { q9_Hand, q9_6_Hand, q6_Hand, q6_3_Hand, q3_Hand };
                }

                // ═══ ИНТЕРПОЛЯЦИЯ (С УЧЕТОМ 1.5 ОБОРОТА) ═══
                // Умножаем на 4 (кол-во сегментов) и делим на 1.5 (полный ход руля)
                float t = (Mathf.Abs(steerValue) / steeringSensitivity) * 4f;

                // Ограничиваем, чтобы не выйти за пределы массива
                t = Mathf.Clamp(t, 0f, 4f);

                int idx = Mathf.FloorToInt(t);
                float frac = t - idx;

                if (idx >= 4) { idx = 3; frac = 1f; }

                Quaternion targetUp = Quaternion.Slerp(upArray[idx], upArray[idx + 1], frac);
                Quaternion targetFore = Quaternion.Slerp(foreArray[idx], foreArray[idx + 1], frac);
                Quaternion targetHand = Quaternion.Slerp(handArray[idx], handArray[idx + 1], frac);

                // ═══ КЛЮЧИЦА ═══
                Quaternion targetClav;
                float tClav = Mathf.Clamp01(t / 2f);
                if (steerValue >= 0)
                    targetClav = Quaternion.Slerp(q9_Clav, q12_Clav, tClav);
                else
                    targetClav = Quaternion.Slerp(q9_Clav, q6_Clav, tClav);

                // ═══ ВЕСА ДЛЯ ДОТЯЖКИ ═══
                float weightToExtremes = Mathf.Clamp01(1f - Mathf.Abs(t - 2f) / 2f);

                float topWeight = (steerValue >= 0) ? weightToExtremes : 0f;
                float bottomWeight = (steerValue < 0) ? weightToExtremes : 0f;

                if (_lClavicle != null) _lClavicle.localRotation = targetClav;
                if (_lForearm != null) _lForearm.localRotation = targetFore;
                if (_lHand != null) _lHand.localRotation = targetHand;

                if (_lUpperArm != null)
                {
                    _lUpperArm.localRotation = targetUp;
                    Vector3 totalOffset = (topArmOffset * topWeight) + (bottomArmOffset * bottomWeight);
                    _lUpperArm.localPosition = _lUpperArmBasePos + totalOffset;
                }
            }
        }

        private void CacheArmBones()
        {
            if (currentCar == null) return;

            var visual = transform.Find("Visual");
            if (visual != null)
            {
                _lClavicle = FindBoneByName(visual, "L_Clavicle");
                _lUpperArm = FindBoneByName(visual, "L_UpperArm");
                if (_lUpperArm != null) _lUpperArmBasePos = _lUpperArm.localPosition;
                _lForearm = FindBoneByName(visual, "L_Forearm");
                _lHand = FindBoneByName(visual, "L_Hand");

                if (_lUpperArm != null) Debug.Log("[V] ✅ Кости руки найдены!");
            }
        }

        private Transform FindBoneByName(Transform root, string namePart)
        {
            if (root.name.ToLower().Contains(namePart.ToLower())) return root;
            foreach (Transform child in root)
            {
                var res = FindBoneByName(child, namePart);
                if (res != null) return res;
            }
            return null;
        }

        // ОБЩИЙ МЕТОД ПОСАДКИ
        private bool EnterCarBase(Transform seat, bool isPassenger)
        {
            if (inCar || seat == null || currentCar == null)
            {
                Debug.LogError(
                    "[V] EnterCarBase отказ: inCar=" + inCar +
                    ", seatFound=" + (seat != null) +
                    ", currentCarFound=" + (currentCar != null)
                );
                return false;
            }

            carSeat = seat;
            _isPassengerSeat = isPassenger;
            if (cc != null) cc.enabled = false;

            transform.SetParent(currentCar.transform, false);

            // Запоминаем точку сиденья относительно корпуса машины (один раз)
            _seatLocalPos = currentCar.transform.InverseTransformPoint(carSeat.position);

            // Применяем ПОЗИЦИЮ
            Vector3 offset = _isPassengerSeat ? PassengerSeatOffset : DriverSeatOffset;
            transform.localPosition = _seatLocalPos + offset;

            // Применяем МАСШТАБ
            float currentScale = _isPassengerSeat ? PassengerSeatScale : DriverSeatScale;
            transform.localScale = new Vector3(preCarScale.x * currentScale, preCarScale.y * currentScale, preCarScale.z * currentScale);

            transform.localRotation = Quaternion.Euler(0f, 0f, 0f);

            inCar = true;

            // ═══ ВКЛЮЧАЕМ АНИМАЦИЮ ВОДИТЕЛЯ ═══
            if (_animator != null)
            {
                _animator.SetFloat("Speed", 0f); // Останавливаем ходьбу
                _animator.SetBool("IsDriving", true); // Включаем позу сидя
            }

            CacheCarComponents();
            CacheArmBones();
            return true;
        }

        // Посадить за руль (F10)
        public bool EnterCar()
        {
            if (inCar || _isEnteringCar) return false;
            GameObject car = FindNearestCar(8f);
            if (car == null) return false;

            if (following)
            {
                ToggleFollow();
                Debug.Log("[V] Следование остановлено перед посадкой.");
            }

            currentCar = car;

            currentCar = car;
            preCarPosition = transform.position;
            preCarScale = transform.localScale;

            StartCoroutine(EnterCarSmoothRoutine(false));
            return true;
        }

        // Посадить на пассажирское место (F12)
        public bool EnterCarAsPassenger()
        {
            if (inCar || _isEnteringCar) return false;
            GameObject car = FindNearestCar(8f);
            if (car == null) return false;

            if (following)
            {
                ToggleFollow();
                Debug.Log("[V] Следование остановлено перед посадкой.");
            }

            currentCar = car;

            currentCar = car;
            preCarPosition = transform.position;
            preCarScale = transform.localScale;

            StartCoroutine(EnterCarSmoothRoutine(true));
            return true;
        }

        // ═══ МЕТОД: Посадить игрока на пассажирское место ═══
        public bool SitPlayerAsPassenger()
        {
            if (_playerSitting) { ExitPlayerFromPassenger(); return true; }

            var player = GameObject.Find("PLAYER");
            if (player == null) return false;

            GameObject car = FindNearestCar(8f);
            if (car == null) return false;

            Transform passengerSeat = FindPassengerSeat(car);
            if (passengerSeat == null) return false;

            _playerTransform = player.transform;
            _playerCar = car;
            _playerSeat = passengerSeat;
            _playerOriginalPos = player.transform.position;
            _playerOriginalRot = player.transform.rotation;

            // Запоминаем нормальные размеры капсулы
            var cc = player.GetComponent<CharacterController>();
            if (cc != null)
            {
                _originalCCHeight = cc.height;
                _originalCCCenter = cc.center;
                cc.enabled = false;
            }

            Vector3 seatLocalPos = car.transform.InverseTransformPoint(passengerSeat.position);
            Vector3 finalLocalPos = seatLocalPos + PlayerPassengerOffset;
            player.transform.position = car.transform.TransformPoint(finalLocalPos);
            player.transform.rotation = car.transform.rotation * Quaternion.Euler(PlayerPassengerRotation);
            _lastCarRot = car.transform.rotation;

            var rb = player.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = true; rb.velocity = Vector3.zero; }

            _playerSitting = true;
            return true;
        }

        private void ResetPlayerCrouch(GameObject player)
        {
            // Сбрасываем флаги приседания через рефлексию
            Component state = FindComponentByTypeName(player, "PlayerState");
            if (state != null)
            {
                SetField(state, "crouching", false);
                SetField(state, "crouch", false);
                SetField(state, "crouchAmount", 0f);
                SetField(state, "isCrouching", false);
            }

            // Ищем скрипт самого игрока (не PlayerState, а PlayerCharacter или подобный)
            // В MSC часто класс называется "Player" или "PlayerCharacter"
            Component playerScript = FindComponentByTypeName(player, "Player");
            if (playerScript == null) playerScript = FindComponentByTypeName(player, "PlayerCharacter");

            if (playerScript != null)
            {
                // Пытаемся вызвать метод вставания, если он есть
                try
                {
                    var m = playerScript.GetType().GetMethod("SetCrouch", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (m != null) m.Invoke(playerScript, new object[] { false });

                    m = playerScript.GetType().GetMethod("ForceStand", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (m != null) m.Invoke(playerScript, null);
                }
                catch { }
            }

            // Возвращаем стандартную высоту на всякий случай (но не жестко 1.8, а из игры, если есть)
            var cc = player.GetComponent<CharacterController>();
            if (cc != null)
            {
                // Просто применяем Move, чтобы обновить коллайдер
                cc.Move(Vector3.zero);
            }
        }

        public void ExitPlayerFromPassenger()
        {
            if (!_playerSitting) return;
            _playerSitting = false;

            var player = GameObject.Find("PLAYER");
            if (player != null)
            {
                if (_playerCar != null)
                {
                    Vector3 exitPos = _playerCar.transform.position + _playerCar.transform.right * 3f + Vector3.up * 1.2f;
                    player.transform.position = exitPos;
                    float yaw = _playerCar.transform.eulerAngles.y;
                    player.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                }
                else
                {
                    player.transform.position = _playerOriginalPos;
                    player.transform.rotation = _playerOriginalRot;
                }

                // Возвращаем капсуле нормальные размеры
                var cc = player.GetComponent<CharacterController>();
                if (cc != null)
                {
                    cc.height = _originalCCHeight;
                    cc.center = _originalCCCenter;
                    cc.enabled = true;
                }

                var rb = player.GetComponent<Rigidbody>();
                if (rb != null) { rb.isKinematic = false; rb.velocity = Vector3.zero; }

                // Сбрасываем флаги, чтобы игра поняла, что мы стоим
                ResetPlayerCrouch(player);
            }

            _playerCar = null;
            _playerSeat = null;
        }

        public void ExitCar()
        {
            if (!inCar) return;

            // ═══ ВЫКЛЮЧАЕМ АНИМАЦИЮ ВОДИТЕЛЯ ═══
            if (_animator != null)
            {
                _animator.SetBool("IsDriving", false); // Возвращаемся в Idle
            }

            ReleaseInput();
            EnableAutoShift(false);
            StopPlayback();
            if (_recording) StopRecording();

            transform.SetParent(null, true);

            // ВОЗВРАЩАЕМ НОРМАЛЬНЫЙ РАЗМЕР
            transform.localScale = preCarScale;

            Vector3 exitPos;
            if (currentCar != null)
                exitPos = currentCar.transform.position + currentCar.transform.right * -0.5f + Vector3.up * 1.2f;
            else
                exitPos = preCarPosition + Vector3.up * 1.2f;

            transform.position = exitPos;
            if (cc != null) cc.enabled = true;

            inCar = false;
            currentCar = null;
            carSeat = null;
        }
    }

    // Отдельный компонент на дочернем trigger-коллайдере.
    // Он сообщает VController о входе движущегося Rigidbody в капсулу V.
    public class VImpactSensor : MonoBehaviour
    {
        private VController _controller;
        private readonly Dictionary<int, float> _lastImpactTimes =
            new Dictionary<int, float>();

        public void Initialize(VController controller)
        {
            _controller = controller;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_controller == null || other == null)
                return;

            Rigidbody body = other.attachedRigidbody;
            if (body == null || body.isKinematic)
                return;

            int bodyId = body.GetInstanceID();
            float lastImpactTime;

            if (_lastImpactTimes.TryGetValue(bodyId, out lastImpactTime) &&
                Time.time - lastImpactTime < 0.25f)
            {
                return;
            }

            _lastImpactTimes[bodyId] = Time.time;
            _controller.RegisterExternalImpact(body);

            if (_lastImpactTimes.Count > 64)
            {
                List<int> expired = new List<int>();
                foreach (KeyValuePair<int, float> entry in _lastImpactTimes)
                {
                    if (Time.time - entry.Value > 2f)
                        expired.Add(entry.Key);
                }

                foreach (int id in expired)
                    _lastImpactTimes.Remove(id);
            }
        }
    }
}
