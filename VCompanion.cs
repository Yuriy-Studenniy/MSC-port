using MSCLoader;
using UnityEngine;
using System;
using System.IO;
using System.Threading;
using System.Net;
using System.Text;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace V_Companion
{
    public class VCompanion : Mod
    {
        public override string ID => "V_Companion";
        public override string Name => "V - AI Companion";
        public override string Version => "0.15";
        public override string Author => "you";
        public override string Description => "AI-компаньон для MSC. Часть кода сгенерирована с помощью AI.";

        private const string SERVER_URL = "http://127.0.0.1:8000/v/decide";

        private const string TOOL_RESULT_URL =
            "http://127.0.0.1:8000/v/tool_result";

        // Отдельный endpoint Silero TTS. Генерация выполняется
        // в фоновом потоке, чтобы не подвешивать Unity Update.
        private const string TTS_URL =
            "http://127.0.0.1:8000/v/tts";

        private GameObject vPuppet;
        private VController vController;

        // Новый исполнитель инструментов V-Agent.
        private VToolDispatcher _toolDispatcher;

        // ═══ АВТОНОМНЫЙ НАБЛЮДАТЕЛЬ ═══

        private readonly VGameObserver _gameObserver =
            new VGameObserver();

        private string _pendingGameEvent = "";

        // Сколько запросов к серверу сейчас выполняется.
        private int _serverRequestsInFlight = 0;

        // Периодическая инициатива V, когда игрок некоторое время
        // не обращался непосредственно к V. Движение и обычные игровые
        // действия игрока этот таймер не сбрасывают.
        private bool _idleAutonomyEnabled = true;
        private bool _hasPlayerContactTimestamp = false;
        private float _lastPlayerContactTime = 0f;
        private float _nextIdleAutonomyCheckTime = 0f;
        private bool _idleAutonomyRequestInFlight = false;
        private float _nextIdleAutonomyUpdateTime = 0f;
        private const float IDLE_AUTONOMY_DELAY = 45f;
        private const float IDLE_AUTONOMY_INTERVAL = 120f;
        private const float IDLE_AUTONOMY_UPDATE_INTERVAL = 1f;

        private readonly Queue<string> responseQueue = new Queue<string>();
        private readonly Queue<string> errorQueue = new Queue<string>();
        private readonly object queueLock = new object();
        private readonly Queue<string> audioQueue = new Queue<string>();

        // Последовательная генерация голоса: HTTP-запросы TTS не
        // выполняются из Unity Update и не создают поток на каждую реплику.
        private readonly Queue<string> _ttsTextQueue = new Queue<string>();
        private readonly Queue<string> _ttsAudioPathQueue = new Queue<string>();
        private readonly Queue<string> _ttsErrorQueue = new Queue<string>();
        private bool _ttsSenderRunning = false;

        // Последовательная отправка результатов инструментов Python.
        private readonly Queue<string> _toolResultSendQueue =
            new Queue<string>();

        private readonly Queue<string> _toolTransportErrorQueue =
            new Queue<string>();

        private readonly Queue<string> agentResponseQueue =
            new Queue<string>();

        private bool _toolResultSenderRunning = false;

        private string statusMessage = "";
        private float statusTimeout = 0f;
        private bool inputVisible = false;
        private string inputText = "";

        private bool _namingRoute = false;
        private string _routeNameInput = "";

        private AudioSource audioSource;
        private GameObject vModelPrefab;

        private RuntimeAnimatorController vAnimatorController;

        private bool _seatMenuOpen = false;
        // ═══ МЕНЮ DESTINATIONS ═══
        private bool _menuOpen = false;
        private List<string> _menuItems = new List<string>();
        private int _menuSelected = 0;

        public override void ModSetup()
        {
            SetupFunction(Setup.OnLoad, Mod_OnLoad);
            SetupFunction(Setup.Update, Mod_Update);
            SetupFunction(Setup.OnGUI, Mod_OnGUI);
        }

        private void Mod_OnLoad()
        {
            var audioGo = new GameObject("V_Audio");
            UnityEngine.Object.DontDestroyOnLoad(audioGo);
            audioSource = audioGo.AddComponent<AudioSource>();
            audioSource.spatialBlend = 0f;
            audioSource.volume = 1f;
            audioSource.playOnAwake = false;

            // ═══ ЗАГРУЗКА МОДЕЛИ НАПРЯМУЮ ЧЕРЕЗ UNITY ═══
            try
            {
                // Путь к папке Mods (там, где лежит V_Companion.dll)
                string modDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

                // Склеиваем путь по частям, так как старый .NET не умеет 4 аргумента
                string assetsDir = System.IO.Path.Combine(modDir, "Assets");
                string compDir = System.IO.Path.Combine(assetsDir, "V_Companion");
                string bundlePath = System.IO.Path.Combine(compDir, "vmodel.unity3d");

                if (System.IO.File.Exists(bundlePath))
                {
                    // В Unity 5.0 используется CreateFromFile
                    AssetBundle ab = AssetBundle.CreateFromFile(bundlePath);
                    if (ab != null)
                    {
                        vModelPrefab = ab.LoadAsset<GameObject>("v_model");
                        vAnimatorController = ab.LoadAsset<RuntimeAnimatorController>("v_animator"); // Имя файла контроллера в Unity
                        ab.Unload(false); // Оставляем текстуры и меши в памяти

                        if (vModelPrefab != null)
                            ModConsole.Print("[V] ✅ Модель успешно загружена!");
                        else
                            ModConsole.Print("[V] ❌ Префаб 'v_model' не найден внутри бандла! (Проверь имя префаба в Unity)");
                    }
                    else
                    {
                        ModConsole.Print("[V] ❌ Unity не смогла прочитать AssetBundle!");
                    }
                }
                else
                {
                    ModConsole.Print("[V] ❌ Файл не найден по пути: " + bundlePath);
                }
            }
            catch (System.Exception e)
            {
                ModConsole.Print("[V] ❌ Ошибка при загрузке модели: " + e.Message);
            }
            // ═════════════════════════════════════════

            ModConsole.Print("[V] Загружена.");
            ModConsole.Print("F2-трогаться F4-граф F5-спавн F6-меню F7-follow Ctrl+F7-автономность Ctrl/Alt+F8-тест F8-чат F9-открыть F10-машина F11-кинуть");
            SetStatus("V готова. F8 - поговорить.");
        }

        private void Mod_Update()
        {
            // Таймер инициативы сбрасывается только при прямом обращении
            // игрока к V, а не при обычном игровом вводе или перемещении.
            List<string> completedErrors = new List<string>();
            List<string> completedResponses = new List<string>();
            List<string> completedAgentResponses =
                new List<string>();
            List<string> completedToolTransportErrors =
                new List<string>();
            List<string> completedTtsAudioPaths = new List<string>();
            List<string> completedTtsErrors = new List<string>();

            // Забираем ответы из очередей.
            // Саму обработку выполняем уже после освобождения блокировки.
            lock (queueLock)
            {
                while (errorQueue.Count > 0)
                {
                    if (_serverRequestsInFlight > 0)
                        _serverRequestsInFlight--;

                    completedErrors.Add(errorQueue.Dequeue());
                }

                while (responseQueue.Count > 0)
                {
                    if (_serverRequestsInFlight > 0)
                        _serverRequestsInFlight--;

                    completedResponses.Add(responseQueue.Dequeue());
                }

                while (agentResponseQueue.Count > 0)
                {
                    completedAgentResponses.Add(
                        agentResponseQueue.Dequeue()
                    );
                }

                while (_toolTransportErrorQueue.Count > 0)
                {
                    completedToolTransportErrors.Add(
                        _toolTransportErrorQueue.Dequeue()
                    );
                }

                // Unity AudioClip/AudioSource трогаем только в главном потоке.
                while (_ttsAudioPathQueue.Count > 0)
                    completedTtsAudioPaths.Add(_ttsAudioPathQueue.Dequeue());

                while (_ttsErrorQueue.Count > 0)
                    completedTtsErrors.Add(_ttsErrorQueue.Dequeue());
            }

            foreach (string path in completedTtsAudioPaths)
                audioQueue.Enqueue(path);

            foreach (string error in completedTtsErrors)
            {
                ModConsole.Print("[V][TTS] Ошибка генерации голоса: " + error);
            }

            foreach (string response in completedAgentResponses)
            {
                HandleServerResponse(response);
            }

            foreach (string error in completedToolTransportErrors)
            {
                SetStatus(
                    "❌ V-Agent: ошибка отправки результата: " + error,
                    8f
                );
            }

            // Обрабатываем ошибки вне блокировки.
            foreach (string error in completedErrors)
            {
                if (_idleAutonomyRequestInFlight)
                {
                    _idleAutonomyRequestInFlight = false;
                    _nextIdleAutonomyCheckTime = Time.time + 15f;
                    ModConsole.Print("[V][AUTONOMY] Ошибка запроса: " + error + ". Повторю через 15 секунд.");
                }

                SetStatus("❌ " + error, 5f);
            }

            // Обрабатываем ответы вне блокировки.
            foreach (string response in completedResponses)
            {
                HandleServerResponse(response);
            }

            // ═══ V-AGENT TOOL DISPATCHER ═══

            // Если V уже создана, создаём диспетчер.
            if (vController != null && _toolDispatcher == null)
            {
                _toolDispatcher = new VToolDispatcher(vController);
            }

            // Временный тест обратного канала C# -> Python.
            if (Input.GetKey(KeyCode.LeftControl) &&
                Input.GetKey(KeyCode.LeftShift) &&
                Input.GetKeyDown(KeyCode.K))
            {
                _toolDispatcher.Dispatch(
                    "manual_csharp_test_001",
                    "get_game_state",
                    "driver",
                    "",
                    "",
                    15f,
                    10
                );

                ModConsole.Print("[V][TEST] Проверка отправки результата.");
            }

            // Обновляем длительные действия.
            if (_toolDispatcher != null)
            {
                _toolDispatcher.Tick();

                // Забираем результаты выполнения.
                List<string> toolResults =
                    _toolDispatcher.DrainResults();

                foreach (string result in toolResults)
                {
                    ModConsole.Print(
                        "[V][TOOL RESULT] " + result
                    );

                    QueueToolResultToServer(result);
                }
            }

            // ═══ АВТОНОМНОЕ НАБЛЮДЕНИЕ ЗА ПОЕЗДКОЙ ═══

            if (vController != null)
            {
                string detectedEvent =
                    _gameObserver.Tick(vController, Time.time);

                if (!string.IsNullOrEmpty(detectedEvent))
                {
                    _pendingGameEvent = detectedEvent;

                    ModConsole.Print(
                        "[V][OBSERVER] " + detectedEvent
                    );
                }
            }

            // Отправляем событие, когда предыдущие запросы завершились.
            if (!string.IsNullOrEmpty(_pendingGameEvent) &&
                _serverRequestsInFlight == 0)
            {
                string eventToSend = _pendingGameEvent;
                _pendingGameEvent = "";

                SendGameEventToV(eventToSend);
            }

            if (vController != null && vController.IsAwaitingRouteName)
            {
                if (!_namingRoute) { _namingRoute = true; _routeNameInput = ""; }

                string typed = Input.inputString;
                if (!string.IsNullOrEmpty(typed))
                {
                    foreach (char c in typed)
                    {
                        if (c == '\b')
                        {
                            if (_routeNameInput.Length > 0)
                                _routeNameInput = _routeNameInput.Substring(0, _routeNameInput.Length - 1);
                        }
                        else if (c == '\n' || c == '\r')
                        {
                            if (!string.IsNullOrEmpty(_routeNameInput.Trim()))
                            {
                                vController.SaveRouteWithName(_routeNameInput.Trim());
                                SetStatus("Маршрут сохранён: " + _routeNameInput.Trim(), 5f);
                            }
                            MarkPlayerContactWithV();
                            _namingRoute = false;
                            _routeNameInput = "";
                            break;
                        }
                        else if (!char.IsControl(c))
                        {
                            _routeNameInput += c;
                        }
                    }
                }
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    vController.CancelSaveRoute();
                    MarkPlayerContactWithV();
                    _namingRoute = false;
                    _routeNameInput = "";
                    SetStatus("Сохранение отменено");
                }
                if (statusTimeout > 0) { statusTimeout -= Time.deltaTime; if (statusTimeout <= 0) statusMessage = ""; }
                return;
            }
            _namingRoute = false;

            if (_menuOpen)
            {
                if (Input.GetKeyDown(KeyCode.UpArrow) && _menuSelected > 0) _menuSelected--;
                if (Input.GetKeyDown(KeyCode.DownArrow) && _menuSelected < _menuItems.Count - 1) _menuSelected++;
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    if (_menuItems.Count > 0 && vController != null)
                    {
                        string dest = _menuItems[_menuSelected];
                        _menuOpen = false;
                        MarkPlayerContactWithV();
                        if (vController.StartNavigationTo(dest))
                            SetStatus("Еду в " + dest);
                        else
                            SetStatus("Не нашла путь в " + dest);
                    }
                }
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    _menuOpen = false;
                    MarkPlayerContactWithV();
                }
                if (statusTimeout > 0) { statusTimeout -= Time.deltaTime; if (statusTimeout <= 0) statusMessage = ""; }
                return;
            }

            if (inputVisible)
            {
                Input.ResetInputAxes();
            }
            else
            {
                if (Input.GetKeyDown(KeyCode.F2)) TestMoveOff();
                if (Input.GetKeyDown(KeyCode.F4)) DumpGraph();
                if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.G))
                {
                    if (GraphVisualizer.Instance == null)
                        SetStatus("Сначала F4 — построй граф");
                    else
                    {
                        GraphVisualizer.Instance.CycleMode();
                        SetStatus("Graph: " + GraphVisualizer.Instance.ModeName);
                    }
                }
                if (Input.GetKeyDown(KeyCode.F5)) SpawnV();
                if (Input.GetKeyDown(KeyCode.F6)) OpenNavMenu();
                if ((Input.GetKey(KeyCode.LeftControl) ||
                     Input.GetKey(KeyCode.RightControl)) &&
                    Input.GetKeyDown(KeyCode.F7))
                {
                    _idleAutonomyEnabled = !_idleAutonomyEnabled;
                    MarkPlayerContactWithV();
                    _nextIdleAutonomyCheckTime = Time.time + IDLE_AUTONOMY_DELAY;
                    SetStatus(
                        "Свободная инициатива V: " +
                        (_idleAutonomyEnabled ? "ВКЛ" : "ВЫКЛ")
                    );
                    ModConsole.Print(
                        "[V][AUTONOMY] " +
                        (_idleAutonomyEnabled ? "enabled" : "disabled")
                    );
                }
                else if ((Input.GetKey(KeyCode.LeftControl) ||
                          Input.GetKey(KeyCode.RightControl) ||
                          Input.GetKey(KeyCode.LeftAlt) ||
                          Input.GetKey(KeyCode.RightAlt)) &&
                         Input.GetKeyDown(KeyCode.F8))
                {
                    if (!_idleAutonomyEnabled)
                    {
                        ModConsole.Print("[V][AUTONOMY] Тест не запущен: автономность выключена (Ctrl+F7).");
                    }
                    else if (_serverRequestsInFlight > 0)
                    {
                        ModConsole.Print("[V][AUTONOMY] Тест не запущен: уже есть запрос к серверу.");
                    }
                    else if (IsVBusyForIdleAutonomy())
                    {
                        ModConsole.Print(
                            "[V][AUTONOMY] Тест не запущен: " +
                            GetVBusyReasonForIdleAutonomy() + "."
                        );
                    }
                    else
                    {
                        MarkPlayerContactWithV();
                        _nextIdleAutonomyCheckTime = Time.time + IDLE_AUTONOMY_INTERVAL;
                        ModConsole.Print("[V][AUTONOMY] Ручной тест инициативы (Ctrl/Alt+F8).");
                        SendIdleAutonomyTick(true);
                    }
                }
                else if (Input.GetKeyDown(KeyCode.F7)) ToggleFollow();
                if (Input.GetKeyDown(KeyCode.F9)) InteractNearest("OPEN");
                if (Input.GetKeyDown(KeyCode.F10)) ToggleCar();
                if (Input.GetKeyDown(KeyCode.F11)) DoThrow();

                if (Input.GetKeyDown(KeyCode.PageUp))
                {
                    if (vController != null)
                    {
                        MarkPlayerContactWithV();
                        vController.ToggleRecording();
                        if (vController.IsRecording) SetStatus("🔴 ЗАПИСЬ включена");
                        else SetStatus("Введи имя маршрута...", 30f);
                    }
                }
                if (Input.GetKeyDown(KeyCode.PageDown))
                {
                    if (vController != null)
                    {
                        MarkPlayerContactWithV();
                        vController.ToggleRoutePlayback();
                        SetStatus(vController.IsPlayingRoute ? "▶️ Воспроизведение" : "⏹️ Стоп");
                    }
                }


                if (Input.GetKeyDown(KeyCode.Alpha1) && vController != null) vController.DebugThrottle(1.0f);
                if (Input.GetKeyDown(KeyCode.Alpha2) && vController != null) vController.DebugBrake(1.0f);
                if (Input.GetKeyDown(KeyCode.Alpha3) && vController != null) vController.DebugSteer(-0.5f);
                if (Input.GetKeyDown(KeyCode.Alpha4) && vController != null) vController.DebugSteer(0.5f);
                if (Input.GetKeyDown(KeyCode.Alpha5) && vController != null) vController.DebugShift(1);
                if (Input.GetKeyDown(KeyCode.Alpha6) && vController != null) vController.DebugShift(2);
                if (Input.GetKeyDown(KeyCode.Alpha7) && vController != null) vController.DebugShift(3);
                if (Input.GetKeyDown(KeyCode.Alpha8) && vController != null) vController.DebugShift(0);
                if (Input.GetKeyDown(KeyCode.Alpha9) && vController != null) vController.DebugLogState();
                if (Input.GetKeyDown(KeyCode.Alpha0) && vController != null) { vController.ReleaseInput(); SetStatus("Ввод сброшен"); }
                if (Input.GetKeyDown(KeyCode.T) && vController != null)
                {
                    bool ns = !vController.IsAutoShiftEnabled;
                    vController.EnableAutoShift(ns);
                    SetStatus("Автошифт: " + (ns ? "ВКЛ" : "ВЫКЛ"));
                }
            }

            if (Input.GetKeyDown(KeyCode.F3))
            {
                _seatMenuOpen = !_seatMenuOpen;
                MarkPlayerContactWithV();
            }

            if (!Input.GetKey(KeyCode.LeftControl) &&
                !Input.GetKey(KeyCode.RightControl) &&
                !Input.GetKey(KeyCode.LeftAlt) &&
                !Input.GetKey(KeyCode.RightAlt) &&
                Input.GetKeyDown(KeyCode.F8))
            {
                inputVisible = !inputVisible;
                MarkPlayerContactWithV();
                if (inputVisible) inputText = "";
            }

            if (Input.GetKeyDown(KeyCode.F1)) ToggleCar(); // Это водительское

            if (Input.GetKeyDown(KeyCode.F12)) // А это пассажирское
            {
                if (vController == null) { SetStatus("Сначала F5."); return; }
                MarkPlayerContactWithV();
                if (vController.InCar) { vController.ExitCar(); SetStatus("V вышла."); }
                else
                {
                    if (vController.EnterCarAsPassenger()) SetStatus("V села на пассажирское.");
                    else SetStatus("Нет машины или пассажирского места.");
                }
            }

            if (Input.GetKeyDown(KeyCode.L))
            {
                if (vController != null)
                {
                    if (vController.SitPlayerAsPassenger())
                    {
                        SetStatus("Ты сел на пассажирское место. Нажми F10 для V.");
                        // Опционально: можно автоматически посадить V за руль, когда ты сел пассажиром
                        // if (!vController.InCar) vController.EnterCar(); 
                    }
                    else SetStatus("Рядом нет машины с пассажирским местом.");
                }
            }

            if (inputVisible)
            {
                string typed = Input.inputString;
                if (!string.IsNullOrEmpty(typed))
                {
                    foreach (char c in typed)
                    {
                        if (c == '\b') { if (inputText.Length > 0) inputText = inputText.Substring(0, inputText.Length - 1); }
                        else if (c == '\n' || c == '\r') { if (!string.IsNullOrEmpty(inputText.Trim())) SendMessageToV(inputText.Trim()); inputVisible = false; inputText = ""; break; }
                        else if (!char.IsControl(c)) inputText += c;
                    }
                }
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    if (!string.IsNullOrEmpty(inputText.Trim())) SendMessageToV(inputText.Trim());
                    inputVisible = false; inputText = "";
                }
            }

            // Проверяем инициативу после обработки ввода в этом кадре,
            // чтобы прямое обращение к V не проиграло гонку таймера.
            UpdateIdleAutonomy();

            while (audioQueue.Count > 0) LoadAndPlayAudio(audioQueue.Dequeue());

            if (statusTimeout > 0) { statusTimeout -= Time.deltaTime; if (statusTimeout <= 0) statusMessage = ""; }
        }

        private void Mod_OnGUI()
        {
            if (!string.IsNullOrEmpty(statusMessage))
                GUI.Label(new Rect(10, 10, 900, 25), "[V] " + statusMessage);

            if (vController != null && vController.IsRecording)
                GUI.Label(new Rect(10, 35, 300, 25), "🔴 REC");

            if (_namingRoute)
            {
                float w = 640f, h = 100f;
                Rect r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
                GUI.Box(r, "");
                GUI.Label(new Rect(r.x + 15, r.y + 12, w - 30, 22), "Имя маршрута:");
                GUI.Label(new Rect(r.x + 15, r.y + 42, w - 30, 26), "> " + _routeNameInput + "_");
                GUI.Label(new Rect(r.x + 15, r.y + 72, w - 30, 22), "Enter — сохранить, Esc — отмена");
                return;
            }

            if (_menuOpen)
            {
                float w = 400f, h = 80f + _menuItems.Count * 28f;
                Rect r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
                GUI.Box(r, "Куда ехать?");
                for (int i = 0; i < _menuItems.Count; i++)
                {
                    float y = r.y + 32 + i * 28f;
                    bool sel = (i == _menuSelected);
                    GUI.color = sel ? Color.yellow : Color.white;
                    GUI.Label(new Rect(r.x + 15, y, w - 30, 24),
                        (sel ? "► " : "  ") + _menuItems[i]);
                }
                GUI.color = Color.white;
                GUI.Label(new Rect(r.x + 15, r.y + h - 25, w - 30, 22),
                    "↑↓ выбор, Enter — поехали, Esc — отмена");
                return;
            }

            if (_seatMenuOpen)
            {
                // Увеличиваем высоту окна, чтобы влез ползунок скорости
                GUI.Box(new Rect(Screen.width - 320, 50, 300, 560), "Настройки (F3)");

                // ВОДИТЕЛЬ (V)
                GUI.Label(new Rect(Screen.width - 310, 75, 200, 20), "V — ВОДИТЕЛЬ (F10)");
                GUI.Label(new Rect(Screen.width - 310, 95, 30, 20), "X:");
                VController.DriverSeatOffset.x = GUI.HorizontalSlider(new Rect(Screen.width - 280, 100, 150, 20), VController.DriverSeatOffset.x, -2f, 2f);
                GUI.Label(new Rect(Screen.width - 120, 95, 100, 20), VController.DriverSeatOffset.x.ToString("F2"));

                GUI.Label(new Rect(Screen.width - 310, 115, 30, 20), "Y:");
                VController.DriverSeatOffset.y = GUI.HorizontalSlider(new Rect(Screen.width - 280, 120, 150, 20), VController.DriverSeatOffset.y, -2f, 2f);
                GUI.Label(new Rect(Screen.width - 120, 115, 100, 20), VController.DriverSeatOffset.y.ToString("F2"));

                GUI.Label(new Rect(Screen.width - 310, 135, 30, 20), "Z:");
                VController.DriverSeatOffset.z = GUI.HorizontalSlider(new Rect(Screen.width - 280, 140, 150, 20), VController.DriverSeatOffset.z, -2f, 2f);
                GUI.Label(new Rect(Screen.width - 120, 135, 100, 20), VController.DriverSeatOffset.z.ToString("F2"));

                GUI.Label(new Rect(Screen.width - 310, 155, 50, 20), "Размер:");
                VController.DriverSeatScale = GUI.HorizontalSlider(new Rect(Screen.width - 250, 160, 120, 20), VController.DriverSeatScale, 0.1f, 1.5f);
                GUI.Label(new Rect(Screen.width - 120, 155, 100, 20), VController.DriverSeatScale.ToString("F2"));

                // ПАССАЖИР (V)
                GUI.Label(new Rect(Screen.width - 310, 185, 200, 20), "V — ПАССАЖИР (F12)");
                GUI.Label(new Rect(Screen.width - 310, 205, 30, 20), "X:");
                VController.PassengerSeatOffset.x = GUI.HorizontalSlider(new Rect(Screen.width - 280, 210, 150, 20), VController.PassengerSeatOffset.x, -2f, 2f);
                GUI.Label(new Rect(Screen.width - 120, 205, 100, 20), VController.PassengerSeatOffset.x.ToString("F2"));

                GUI.Label(new Rect(Screen.width - 310, 225, 30, 20), "Y:");
                VController.PassengerSeatOffset.y = GUI.HorizontalSlider(new Rect(Screen.width - 280, 230, 150, 20), VController.PassengerSeatOffset.y, -2f, 2f);
                GUI.Label(new Rect(Screen.width - 120, 225, 100, 20), VController.PassengerSeatOffset.y.ToString("F2"));

                GUI.Label(new Rect(Screen.width - 310, 245, 30, 20), "Z:");
                VController.PassengerSeatOffset.z = GUI.HorizontalSlider(new Rect(Screen.width - 280, 250, 150, 20), VController.PassengerSeatOffset.z, -2f, 2f);
                GUI.Label(new Rect(Screen.width - 120, 245, 100, 20), VController.PassengerSeatOffset.z.ToString("F2"));

                GUI.Label(new Rect(Screen.width - 310, 265, 50, 20), "Размер:");
                VController.PassengerSeatScale = GUI.HorizontalSlider(new Rect(Screen.width - 250, 270, 120, 20), VController.PassengerSeatScale, 0.1f, 1.5f);
                GUI.Label(new Rect(Screen.width - 120, 265, 100, 20), VController.PassengerSeatScale.ToString("F2"));

                // ИГРОК НА ПАССАЖИРСКОМ
                GUI.Label(new Rect(Screen.width - 310, 295, 200, 20), "ИГРОК — ПАССАЖИР");
                GUI.Label(new Rect(Screen.width - 310, 315, 30, 20), "X:");
                VController.PlayerPassengerOffset.x = GUI.HorizontalSlider(new Rect(Screen.width - 280, 320, 150, 20), VController.PlayerPassengerOffset.x, -2f, 2f);
                GUI.Label(new Rect(Screen.width - 120, 315, 100, 20), VController.PlayerPassengerOffset.x.ToString("F2"));

                GUI.Label(new Rect(Screen.width - 310, 335, 30, 20), "Y:");
                VController.PlayerPassengerOffset.y = GUI.HorizontalSlider(new Rect(Screen.width - 280, 340, 150, 20), VController.PlayerPassengerOffset.y, -2f, 2f);
                GUI.Label(new Rect(Screen.width - 120, 335, 100, 20), VController.PlayerPassengerOffset.y.ToString("F2"));

                GUI.Label(new Rect(Screen.width - 310, 355, 30, 20), "Z:");
                VController.PlayerPassengerOffset.z = GUI.HorizontalSlider(new Rect(Screen.width - 280, 360, 150, 20), VController.PlayerPassengerOffset.z, -2f, 2f);
                GUI.Label(new Rect(Screen.width - 120, 355, 100, 20), VController.PlayerPassengerOffset.z.ToString("F2"));

                GUI.Label(new Rect(Screen.width - 310, 375, 30, 20), "Y°:");
                VController.PlayerPassengerRotation.y = GUI.HorizontalSlider(new Rect(Screen.width - 280, 380, 150, 20), VController.PlayerPassengerRotation.y, -180f, 180f);
                GUI.Label(new Rect(Screen.width - 120, 375, 100, 20), VController.PlayerPassengerRotation.y.ToString("F0"));

                GUI.Label(new Rect(Screen.width - 310, 395, 30, 20), "X°:");
                VController.PlayerPassengerRotation.x = GUI.HorizontalSlider(new Rect(Screen.width - 280, 400, 150, 20), VController.PlayerPassengerRotation.x, -90f, 90f);
                GUI.Label(new Rect(Screen.width - 120, 395, 100, 20), VController.PlayerPassengerRotation.x.ToString("F0"));

                // ═══ СКОРОСТЬ ХОДЬБЫ V ═══
                GUI.Label(new Rect(Screen.width - 310, 425, 200, 20), "СКОРОСТЬ ХОДЬБЫ V");
                GUI.Label(new Rect(Screen.width - 310, 445, 80, 20), "М/с:");
                VController.WalkSpeed = GUI.HorizontalSlider(new Rect(Screen.width - 250, 450, 120, 20), VController.WalkSpeed, 0.5f, 6.0f);
                GUI.Label(new Rect(Screen.width - 120, 445, 100, 20), VController.WalkSpeed.ToString("F2"));

                // Кнопка вывода логов
                if (GUI.Button(new Rect(Screen.width - 310, 485, 280, 30), "Вывести все цифры в консоль (F1)"))
                {
                    ModConsole.Print("=== НАСТРОЙКИ ===");
                    ModConsole.Print($"V Водитель (Поз): new Vector3({VController.DriverSeatOffset.x:F2}f, {VController.DriverSeatOffset.y:F2}f, {VController.DriverSeatOffset.z:F2}f)");
                    ModConsole.Print($"V Водитель (Размер): {VController.DriverSeatScale:F2}f");
                    ModConsole.Print($"V Пассажир (Поз): new Vector3({VController.PassengerSeatOffset.x:F2}f, {VController.PassengerSeatOffset.y:F2}f, {VController.PassengerSeatOffset.z:F2}f)");
                    ModConsole.Print($"V Пассажир (Размер): {VController.PassengerSeatScale:F2}f");
                    ModConsole.Print($"Игрок Пассажир (Поз): new Vector3({VController.PlayerPassengerOffset.x:F2}f, {VController.PlayerPassengerOffset.y:F2}f, {VController.PlayerPassengerOffset.z:F2}f)");
                    ModConsole.Print($"Игрок Пассажир (Рот): new Vector3({VController.PlayerPassengerRotation.x:F0}f, {VController.PlayerPassengerRotation.y:F0}f, {VController.PlayerPassengerRotation.z:F0}f)");
                    ModConsole.Print($"Скорость ходьбы V: {VController.WalkSpeed:F2}f");
                    SetStatus("Цифры выведены в консоль.");
                }
            }

            // ═══ ОКНО РАЗГОВОРА С V (F8) ═══
            if (inputVisible)
            {
                float w = Mathf.Min(700f, Screen.width - 40f);
                float h = 125f;

                // Располагаем окно внизу по центру экрана
                Rect r = new Rect(
                    (Screen.width - w) / 2f,
                    Screen.height - h - 45f,
                    w,
                    h
                );

                GUI.Box(r, "Чат с V");

                GUI.Label(
                    new Rect(r.x + 15f, r.y + 30f, w - 30f, 25f),
                    "Введи сообщение и нажми Enter. F8 — закрыть."
                );

                // Поле, в котором отображается набираемый текст
                GUI.Box(
                    new Rect(r.x + 15f, r.y + 60f, w - 30f, 30f),
                    ""
                );

                string shownText = string.IsNullOrEmpty(inputText)
                    ? "Начни печатать..."
                    : inputText;

                // Мигающий курсор в конце строки
                if (Time.frameCount % 60 < 30)
                    shownText += "_";

                GUI.Label(
                    new Rect(r.x + 23f, r.y + 63f, w - 46f, 25f),
                    "> " + shownText
                );
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

        private void SpawnV()
        {
            if (vPuppet != null) { SetStatus("V уже здесь."); return; }
            var player = GameObject.Find("PLAYER");
            if (player == null) { SetStatus("PLAYER не найден!"); return; }

            Vector3 pos = player.transform.position + player.transform.forward * 2f;
            vPuppet = new GameObject("V_Puppet");
            vPuppet.transform.position = pos;

            var cc = vPuppet.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.3f;
            cc.center = new Vector3(0f, 0.9f, 0f);
            cc.slopeLimit = 45f;
            cc.stepOffset = 0.4f;

            // Создаём визуал
            if (vModelPrefab != null)
            {
                var visual = UnityEngine.Object.Instantiate(vModelPrefab) as GameObject;
                visual.name = "Visual";

                visual.transform.SetParent(vPuppet.transform, false);
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.Euler(180f, 0f, 0f);

                // ═══ 1. АНИМАТОР ═══
                Animator anim = visual.GetComponentInChildren<Animator>();
                if (anim != null && vAnimatorController != null)
                {
                    anim.runtimeAnimatorController = vAnimatorController;
                    anim.applyRootMotion = false;
                    ModConsole.Print("[V] ✅ Animator Controller применен вручную!");
                }

                // ═══ 2. ХВОСТ ═══
                Transform tailRoot = FindBoneByName(visual.transform, "tail_1");
                if (tailRoot != null)
                {
                    var db = visual.AddComponent<DynamicBone>();
                    db.m_Root = tailRoot;
                    db.m_Damping = 0.27f;
                    db.m_Elasticity = 0.02f;
                    db.m_Stiffness = 0.12f;
                    db.m_Radius = 0.05f;
                    db.m_UpdateRate = 60;

                    var tw = visual.AddComponent<TailWind>();
                    tw.tailBone = db;
                    tw.windSpeed = 1f;
                    tw.windStrength = 0.00087f;

                    ModConsole.Print("[V] ✅ Хвост успешно подключен!");
                }

                // ═══ 3. ВОЗВРАЩАЕМ ТОТ САМЫЙ ФИКС РЕНДЕРА ═══
                foreach (var smr in visual.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    // Заставляем игру всегда рендерить, даже если объект "за экраном"
                    smr.updateWhenOffscreen = true;

                    // Немного расширяем границы (2 метра), чтобы не было багов с отсечением
                    Bounds b = smr.localBounds;
                    b.Expand(2f);
                    smr.localBounds = b;
                }

                // ═══ 4. ВОЗВРАЩАЕМ ДВУХСТОРОННИЕ МАТЕРИАЛЫ ═══
                foreach (var renderer in visual.GetComponentsInChildren<Renderer>())
                {
                    foreach (var mat in renderer.materials)
                    {
                        if (mat.HasProperty("_Cull"))
                        {
                            mat.SetInt("_Cull", 0); // 0 = Cull Off (рисуем обе стороны)
                        }
                    }
                }

                // ═══ 5. УДАЛЯЕМ ФИЗИКУ МОДЕЛИ ═══
                foreach (var col in visual.GetComponentsInChildren<Collider>()) UnityEngine.Object.Destroy(col);
                foreach (var rb in visual.GetComponentsInChildren<Rigidbody>()) UnityEngine.Object.Destroy(rb);
            }
            else
            {
                var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                visual.name = "Visual";
                visual.transform.SetParent(vPuppet.transform, false);
                visual.transform.localPosition = new Vector3(0f, 0.9f, 0f);
                UnityEngine.Object.Destroy(visual.GetComponent<Collider>());
            }

            vController = vPuppet.AddComponent<VController>();
            MarkPlayerContactWithV();
            SetStatus("V заспавнена.");
        }

        private void DespawnV()
        {
            if (vPuppet == null) return;
            UnityEngine.Object.Destroy(vPuppet);
            vPuppet = null; vController = null;
            SetStatus("V исчезла.");
        }

        private void ToggleFollow()
        {
            if (vController == null) { SetStatus("Сначала F5."); return; }
            MarkPlayerContactWithV();
            vController.ToggleFollow();
            SetStatus(vController.IsFollowing ? "V идёт за тобой." : "V стоит.");
        }

        private void DoThrow()
        {
            if (vController == null) { SetStatus("Сначала F5."); return; }
            MarkPlayerContactWithV();
            if (!vController.HasItem) { SetStatus("V ничего не держит."); return; }
            vController.Throw();
            SetStatus("V кинула.");
        }

        private void ToggleCar()
        {
            if (vController == null) { SetStatus("Сначала F5."); return; }
            MarkPlayerContactWithV();
            if (vController.InCar) { vController.ExitCar(); SetStatus("V вышла."); }
            else
            {
                if (vController.EnterCar()) SetStatus("V села.");
                else SetStatus("Нет машины в радиусе 8м.");
            }
        }

        private void InteractNearest(string action)
        {
            if (vController == null) { SetStatus("Сначала F5."); return; }
            MarkPlayerContactWithV();
            string r = vController.InteractWithNearest(action, 5f);
            SetStatus("V: " + r);
        }

        private void TestMoveOff()
        {
            if (vController == null) { SetStatus("Сначала F5."); return; }
            MarkPlayerContactWithV();
            if (!vController.InCar) { SetStatus("V не в машине. F10."); return; }
            if (vController.StartMoveOff()) SetStatus("V поехала");
            else SetStatus("Не смогла начать");
        }

        private void DumpGraph()
        {
            if (vController == null) { SetStatus("Сначала F5."); return; }
            SetStatus("Строю граф...");
            vController.BuildRouteGraph();
            SetStatus("Готово: V_graph.txt + лог");
        }

        private void OpenNavMenu()
        {
            if (vController == null) { SetStatus("Сначала F5."); return; }
            if (!vController.InCar) { SetStatus("V не в машине. F10."); return; }
            var dests = vController.GetAllDestinations();
            if (dests.Count == 0) { SetStatus("В графе нет destinations"); return; }
            MarkPlayerContactWithV();
            _menuItems = dests;
            _menuSelected = 0;
            _menuOpen = true;
        }

        private void SendMessageToV(string message)
        {
            SendMessageToV(message, "");
        }

        private void SendGameEventToV(string eventText)
        {
            SendMessageToV("", eventText);
        }

        private void SendMessageToV(string message, string gameEvent)
        {
            // Автоматические игровые события не считаются общением игрока с V.
            bool isDirectPlayerContact =
                !string.IsNullOrEmpty(message) && !string.IsNullOrEmpty(message.Trim());
            SendAgentRequest(message, gameEvent, "idle", isDirectPlayerContact);
        }

        private void SendIdleAutonomyTick()
        {
            SendIdleAutonomyTick(false);
        }

        private void SendIdleAutonomyTick(bool force)
        {
            string action = force ? "idle_autonomy_force" : "idle_autonomy";
            SendAgentRequest("", "", action, false);
        }

        private void SendAgentRequest(
            string message,
            string gameEvent,
            string playerAction,
            bool playerInitiated)
        {
            if (playerInitiated)
                MarkPlayerContactWithV();

            var player = GameObject.Find("PLAYER");
            if (player == null) { SetStatus("PLAYER не найден."); return; }
            Vector3 pp = player.transform.position;
            Vector3 vp = vPuppet != null ? vPuppet.transform.position : Vector3.zero;
            List<string> nearby = new List<string>();
            if (vController != null)
            {
                try { nearby = vController.GetNearbyContext(15f, 10); }
                catch (Exception e) { ModConsole.Print("[V] nearby: " + e.Message); }
            }
            string nearbyJson = BuildJsonStringArray(nearby);
            // ═══ СОСТОЯНИЕ ИГРЫ ДЛЯ AI-АГЕНТА ═══

            string gameStateJson = "{}";

            if (vController != null)
            {
                Vector3 cp = vController.VehiclePosition;

                gameStateJson = "{"
                    + "\"v_in_car\":" + (vController.InCar ? "true" : "false") + ","
                    + "\"player_is_passenger\":" + (vController.PlayerIsPassenger ? "true" : "false") + ","
                    + "\"seat_role\":" + JsonQuote(vController.SeatRole) + ","
                    + "\"is_entering_car\":" + (vController.IsEnteringCar ? "true" : "false") + ","
                    + "\"v_following\":" + (vController.IsFollowing ? "true" : "false") + ","
                    + "\"has_item\":" + (vController.HasItem ? "true" : "false") + ","

                    + "\"route_active\":" + (vController.IsPlayingRoute ? "true" : "false") + ","
                    + "\"route_preparing\":" + (vController.IsRoutePreparing ? "true" : "false") + ","
                    + "\"route_finishing\":" + (vController.IsFinishingRoute ? "true" : "false") + ","
                    + "\"drive_sequence_active\":" + (vController.IsDrivingSequence ? "true" : "false") + ","

                    + "\"navigation_destination\":" + JsonQuote(vController.NavigationDestination) + ","
                    + "\"current_route_name\":" + JsonQuote(vController.CurrentRouteName) + ","
                    + "\"route_point_index\":" + vController.RoutePointIndex + ","
                    + "\"route_point_count\":" + vController.RoutePointCount + ","
                    + "\"nearest_route_point_distance_m\":" + F(vController.NearestRoutePointDistanceMeters) + ","

                    + "\"vehicle\":{"
                        + "\"available\":" + (vController.HasCurrentVehicle ? "true" : "false") + ","
                        + "\"position\":["
                            + F(cp.x) + "," + F(cp.y) + "," + F(cp.z)
                        + "],"
                        + "\"speed_kmh\":" + F(vController.VehicleSpeedKmh) + ","
                        + "\"vertical_speed_mps\":" + F(vController.VehicleVerticalSpeedMps) + ","
                        + "\"up_dot\":" + F(vController.VehicleUpDot) + ","
                        + "\"tilt_degrees\":" + F(vController.VehicleTiltDegrees)
                    + "}"
                + "}";
            }
            string json = "{"
                + "\"player_pos\":[" + F(pp.x) + "," + F(pp.y) + "," + F(pp.z) + "],"
                + "\"v_pos\":[" + F(vp.x) + "," + F(vp.y) + "," + F(vp.z) + "],"
                + "\"player_action\":" + JsonQuote(playerAction) + ","
                + "\"nearby\":" + nearbyJson + ","
                + "\"game_state\":" + gameStateJson + ","
                + "\"time_of_day\":\"day\","
                + "\"player_message\":" + JsonQuote(message) + ","
                + "\"game_event\":" + JsonQuote(gameEvent) + ","
                + "\"chat_log\":[]"
                + "}";
            if (playerAction == "idle_autonomy" ||
                playerAction == "idle_autonomy_force")
            {
                _idleAutonomyRequestInFlight = true;
            }

            if (playerInitiated)
                SetStatus("Отправляю...", 60f);
            else if (_idleAutonomyRequestInFlight)
                ModConsole.Print("[V][AUTONOMY] Запрос отправлен серверу, жду решение V...");

            _serverRequestsInFlight++;

            try
            {
                Thread t = new Thread(() => DoRequest(json));
                t.IsBackground = true;
                t.Start();
            }
            catch (Exception e)
            {
                if (_serverRequestsInFlight > 0)
                    _serverRequestsInFlight--;

                if (playerAction == "idle_autonomy" ||
                    playerAction == "idle_autonomy_force")
                {
                    _idleAutonomyRequestInFlight = false;
                    _nextIdleAutonomyCheckTime = Time.time + 15f;
                }

                ModConsole.Print("[V][SERVER] Не удалось запустить HTTP-поток: " + e.Message);
            }
        }

        private string GetVBusyReasonForIdleAutonomy()
        {
            if (vController == null) return "VController ещё не создан";

            // Само по себе следование, нахождение в машине
            // или наличие предмета в руках не запрещает V принять решение
            // (например, заговорить). Активные переходы и управление
            // транспортом по-прежнему блокируют новый автономный эпизод.
            if (vController.IsEnteringCar) return "V выполняет посадку в машину";
            if (vController.IsNavigationActive) return "активна навигация";
            if (vController.IsDrivingSequence) return "выполняется последовательность вождения";
            return "";
        }

        private bool IsVBusyForIdleAutonomy()
        {
            return !string.IsNullOrEmpty(GetVBusyReasonForIdleAutonomy());
        }

        private void MarkPlayerContactWithV()
        {
            _lastPlayerContactTime = Time.time;
            _hasPlayerContactTimestamp = true;

            // Любое прямое обращение к V сдвигает следующий возможный тик
            // минимум на 45 секунд. Уже действующий интервал 120 секунд
            // между инициативами при этом не сокращается.
            float contactDueTime = Time.time + IDLE_AUTONOMY_DELAY;
            if (_nextIdleAutonomyCheckTime < contactDueTime)
                _nextIdleAutonomyCheckTime = contactDueTime;
        }

        private void UpdateIdleAutonomy()
        {
            // Эта проверка не должна выполняться каждый кадр: Unity Update
            // вызывается десятки раз в секунду, а таймеру достаточно 1 Гц.
            if (Time.time < _nextIdleAutonomyUpdateTime)
                return;

            _nextIdleAutonomyUpdateTime =
                Time.time + IDLE_AUTONOMY_UPDATE_INTERVAL;

            if (!_idleAutonomyEnabled || vController == null)
                return;

            if (GameObject.Find("PLAYER") == null)
                return;

            // Первый запуск отсчёта начинается после появления V.
            if (!_hasPlayerContactTimestamp)
            {
                MarkPlayerContactWithV();
                return;
            }

            // Не инициируем новую деятельность, пока игрок пользуется
            // интерфейсом V или ещё обрабатывается предыдущий запрос.
            if (inputVisible || _menuOpen || _seatMenuOpen || _namingRoute ||
                vController.IsAwaitingRouteName)
                return;

            if (_idleAutonomyRequestInFlight || _serverRequestsInFlight > 0)
                return;

            if (!string.IsNullOrEmpty(_pendingGameEvent))
                return;

            if (!string.IsNullOrEmpty(GetVBusyReasonForIdleAutonomy()))
                return;

            float secondsWithoutContact = Time.time - _lastPlayerContactTime;
            if (secondsWithoutContact < IDLE_AUTONOMY_DELAY)
                return;

            if (Time.time < _nextIdleAutonomyCheckTime)
                return;

            // Интервал записывается до отправки, чтобы несколько кадров
            // или повторный вызов не породили дублирующие HTTP-запросы.
            _nextIdleAutonomyCheckTime = Time.time + IDLE_AUTONOMY_INTERVAL;
            ModConsole.Print(
                "[V][AUTONOMY] Нет прямого контакта " +
                secondsWithoutContact.ToString("F0") +
                " сек. Отправляю запрос инициативы."
            );
            SendIdleAutonomyTick();
        }

        private string BuildAgentGameStateJson()
        {
            if (vController == null)
                return "{}";

            Vector3 cp = vController.VehiclePosition;

            return "{"
                + "\"v_in_car\":" + (vController.InCar ? "true" : "false") + ","
                + "\"player_is_passenger\":" + (vController.PlayerIsPassenger ? "true" : "false") + ","
                + "\"seat_role\":" + JsonQuote(vController.SeatRole) + ","
                + "\"is_entering_car\":" + (vController.IsEnteringCar ? "true" : "false") + ","
                + "\"v_following\":" + (vController.IsFollowing ? "true" : "false") + ","
                + "\"has_item\":" + (vController.HasItem ? "true" : "false") + ","
                + "\"route_active\":" + (vController.IsPlayingRoute ? "true" : "false") + ","
                + "\"route_preparing\":" + (vController.IsRoutePreparing ? "true" : "false") + ","
                + "\"route_finishing\":" + (vController.IsFinishingRoute ? "true" : "false") + ","
                + "\"navigation_outcome\":" + JsonQuote(vController.NavigationOutcome) + ","
                + "\"navigation_destination\":" + JsonQuote(vController.NavigationDestination) + ","
                + "\"current_route_name\":" + JsonQuote(vController.CurrentRouteName) + ","
                + "\"route_point_index\":" + vController.RoutePointIndex + ","
                + "\"route_point_count\":" + vController.RoutePointCount + ","
                + "\"nearest_route_point_distance_m\":" + F(vController.NearestRoutePointDistanceMeters) + ","
                + "\"vehicle\":{"
                    + "\"available\":" + (vController.HasCurrentVehicle ? "true" : "false") + ","
                    + "\"position\":[" + F(cp.x) + "," + F(cp.y) + "," + F(cp.z) + "],"
                    + "\"speed_kmh\":" + F(vController.VehicleSpeedKmh) + ","
                    + "\"vertical_speed_mps\":" + F(vController.VehicleVerticalSpeedMps) + ","
                    + "\"up_dot\":" + F(vController.VehicleUpDot) + ","
                    + "\"tilt_degrees\":" + F(vController.VehicleTiltDegrees)
                + "}"
            + "}";
        }

        private string BuildToolResultEnvelope(string toolResultJson)
        {
            var player = GameObject.Find("PLAYER");

            Vector3 pp = player != null
                ? player.transform.position
                : Vector3.zero;

            Vector3 vp = vPuppet != null
                ? vPuppet.transform.position
                : Vector3.zero;

            List<string> nearby = new List<string>();

            if (vController != null)
            {
                try
                {
                    nearby = vController.GetNearbyContext(15f, 10);
                }
                catch (Exception e)
                {
                    ModConsole.Print("[V][TOOL] Ошибка nearby: " + e.Message);
                }
            }

            return "{"
                + "\"tool_result\":" + toolResultJson + ","
                + "\"game_state\":" + BuildAgentGameStateJson() + ","
                + "\"player_pos\":["
                    + F(pp.x) + "," + F(pp.y) + "," + F(pp.z)
                + "],"
                + "\"v_pos\":["
                    + F(vp.x) + "," + F(vp.y) + "," + F(vp.z)
                + "],"
                + "\"nearby\":" + BuildJsonStringArray(nearby)
            + "}";
        }

        private void QueueToolResultToServer(string toolResultJson)
        {
            if (string.IsNullOrEmpty(toolResultJson))
                return;

            // Формируем JSON на игровом потоке.
            // Unity API в фоновом потоке не используем.
            string payload = BuildToolResultEnvelope(toolResultJson);

            bool startWorker = false;

            lock (queueLock)
            {
                _toolResultSendQueue.Enqueue(payload);

                if (!_toolResultSenderRunning)
                {
                    _toolResultSenderRunning = true;
                    startWorker = true;
                }
            }

            if (startWorker)
            {
                try
                {
                    Thread t = new Thread(ToolResultSenderLoop);
                    t.IsBackground = true;
                    t.Start();
                }
                catch (Exception e)
                {
                    lock (queueLock)
                    {
                        _toolResultSenderRunning = false;
                        _toolTransportErrorQueue.Enqueue(e.Message);
                    }
                }
            }
        }

        private void ToolResultSenderLoop()
        {
            while (true)
            {
                string payload;

                lock (queueLock)
                {
                    if (_toolResultSendQueue.Count == 0)
                    {
                        _toolResultSenderRunning = false;
                        return;
                    }

                    payload = _toolResultSendQueue.Dequeue();
                }

                try
                {
                    HttpWebRequest req =
                        (HttpWebRequest)WebRequest.Create(TOOL_RESULT_URL);

                    req.Method = "POST";
                    req.ContentType = "application/json";
                    req.Timeout = 15000;
                    req.ReadWriteTimeout = 15000;

                    byte[] body = Encoding.UTF8.GetBytes(payload);
                    req.ContentLength = body.Length;

                    using (var stream = req.GetRequestStream())
                    {
                        stream.Write(body, 0, body.Length);
                    }

                    using (var response = req.GetResponse())
                    using (var reader = new StreamReader(
                        response.GetResponseStream(),
                        Encoding.UTF8))
                    {
                        string responseText = reader.ReadToEnd();

                        // Извлекаем следующее решение Agent Loop.
                        string agentResponse = ExtractObject(
                            responseText,
                            "agent_response"
                        );

                        if (!string.IsNullOrEmpty(agentResponse))
                        {
                            lock (queueLock)
                            {
                                agentResponseQueue.Enqueue(agentResponse);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    lock (queueLock)
                    {
                        _toolTransportErrorQueue.Enqueue(e.Message);
                    }
                }
            }
        }

        private void DoRequest(string json)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(SERVER_URL);
                req.Method = "POST"; req.ContentType = "application/json";
                req.Timeout = 180000; req.ReadWriteTimeout = 180000;
                byte[] body = Encoding.UTF8.GetBytes(json);
                req.ContentLength = body.Length;
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                string respText;
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    respText = sr.ReadToEnd();
                lock (queueLock) responseQueue.Enqueue(respText);
            }
            catch (Exception e) { lock (queueLock) errorQueue.Enqueue(e.Message); }
        }

        private void HandleServerResponse(string json)
        {
            string speech = ExtractString(json, "speech");
            string action = ExtractString(json, "action");
            string target = ExtractString(json, "target");
            string audioPath = ExtractString(json, "audio_path");

            if (!string.IsNullOrEmpty(speech))
            {
                ModConsole.Print("[V] " + speech);
                SetStatus("V: " + speech, 15f);
            }

            if (!string.IsNullOrEmpty(audioPath))
            {
                // Совместимость с сервером, который возвращает готовый WAV.
                audioQueue.Enqueue(audioPath);
            }
            else if (!string.IsNullOrEmpty(speech) && speech.Trim().Length > 0)
            {
                // Текущий V-Agent возвращает speech отдельно от аудио.
                // Запрашиваем Silero явно через /v/tts.
                QueueTtsRequest(speech);
            }

            // Проверяем, содержит ли ответ новый вызов инструмента.
            string toolCallJson = ExtractObject(json, "tool_call");
            bool wasIdleAutonomyResponse = _idleAutonomyRequestInFlight;
            _idleAutonomyRequestInFlight = false;

            if (!string.IsNullOrEmpty(toolCallJson))
            {
                if (vController == null)
                {
                    SetStatus("V-Agent: VController недоступен.");
                    return;
                }

                if (_toolDispatcher == null)
                    _toolDispatcher = new VToolDispatcher(vController);

                string callId = ExtractString(toolCallJson, "call_id");
                string toolName = ExtractString(toolCallJson, "tool");

                string argumentsJson = ExtractObject(
                    toolCallJson,
                    "arguments"
                );

                if (string.IsNullOrEmpty(argumentsJson))
                    argumentsJson = "{}";

                string seatRole = ExtractString(
                    argumentsJson,
                    "seat_role"
                );

                string destination = ExtractString(
                    argumentsJson,
                    "destination"
                );

                string objectId = ExtractString(
                    argumentsJson,
                    "object_id"
                );

                float radius = ExtractFloatValue(
                    argumentsJson,
                    "radius_m",
                    15f
                );

                int limit = ExtractIntValue(
                    argumentsJson,
                    "limit",
                    10
                );

                ModConsole.Print(
                    "[V][AGENT] Вызов инструмента: " + toolName
                );

                _toolDispatcher.Dispatch(
                    callId,
                    toolName,
                    seatRole,
                    destination,
                    objectId,
                    radius,
                    limit
                );

                // Не вызываем старую систему команд одновременно.
                return;
            }

            if (wasIdleAutonomyResponse && string.IsNullOrEmpty(speech))
            {
                ModConsole.Print("[V][AUTONOMY] V получила возможность проявить инициативу и решила пока промолчать.");
                SetStatus("V пока решила ничего не делать.", 6f);
            }

            // Старый формат продолжает работать.
            ExecuteAction(action, target);
        }

        private void ExecuteAction(string action, string target)
        {
            if (vController == null || string.IsNullOrEmpty(action) || action == "none") return;
            switch (action)
            {
                case "follow": vController.SetFollow(true); break;
                case "stop": case "stay": vController.SetFollow(false); break;
                case "come": vController.SetFollow(true); break;
                case "jump": vController.Jump(); break;
                case "pickup":
                case "pick":
                case "take":
                    if (vController.PickupNearby(3.5f)) SetStatus("V подняла."); else SetStatus("Ничего рядом."); break;
                case "drop":
                case "release":
                    if (vController.HasItem) { vController.Drop(); SetStatus("V положила."); }
                    break;
                case "throw":
                    if (vController.HasItem) { vController.Throw(); SetStatus("V кинула."); }
                    break;
                case "enter_car":
                case "enter":
                case "get_in":
                    if (vController.EnterCar()) SetStatus("V села."); else SetStatus("Нет машины."); break;
                case "exit_car":
                case "exit":
                case "get_out":
                    if (vController.InCar) { vController.ExitCar(); SetStatus("V вышла."); }
                    break;
                case "open_door":
                case "open":
                    SetStatus("V: " + vController.InteractWithNearest("OPEN", 5f)); break;
                case "close_door":
                case "close":
                    SetStatus("V: " + vController.InteractWithNearest("CLOSE", 5f)); break;
                case "drive":
                case "go":
                case "move_off":
                case "поехали":
                    if (vController.InCar) { if (vController.StartMoveOff()) SetStatus("V поехала"); }
                    break;
                case "stop_car":
                case "stop_driving":
                    if (vController.InCar) { vController.EnableAutoShift(false); vController.SetThrottle(0f); vController.SetBrake(1f); }
                    break;
            }
        }

        private void QueueTtsRequest(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
                return;

            bool startWorker = false;
            lock (queueLock)
            {
                _ttsTextQueue.Enqueue(text.Trim());
                if (!_ttsSenderRunning)
                {
                    _ttsSenderRunning = true;
                    startWorker = true;
                }
            }

            if (!startWorker)
                return;

            try
            {
                Thread worker = new Thread(TtsSenderLoop);
                worker.IsBackground = true;
                worker.Name = "V_Silero_TTS";
                worker.Start();
            }
            catch (Exception e)
            {
                lock (queueLock)
                {
                    _ttsSenderRunning = false;
                    _ttsErrorQueue.Enqueue("не удалось запустить поток: " + e.Message);
                }
            }
        }

        private void TtsSenderLoop()
        {
            while (true)
            {
                string text;
                lock (queueLock)
                {
                    if (_ttsTextQueue.Count == 0)
                    {
                        _ttsSenderRunning = false;
                        return;
                    }

                    text = _ttsTextQueue.Dequeue();
                }

                try
                {
                    string payload = "{\"text\":" + JsonQuote(text) + "}";
                    byte[] body = Encoding.UTF8.GetBytes(payload);

                    HttpWebRequest req =
                        (HttpWebRequest)WebRequest.Create(TTS_URL);
                    req.Method = "POST";
                    req.ContentType = "application/json";
                    req.Timeout = 240000;
                    req.ReadWriteTimeout = 240000;
                    req.ContentLength = body.Length;

                    using (Stream stream = req.GetRequestStream())
                        stream.Write(body, 0, body.Length);

                    string responseText;
                    using (WebResponse response = req.GetResponse())
                    using (StreamReader reader = new StreamReader(
                        response.GetResponseStream(), Encoding.UTF8))
                    {
                        responseText = reader.ReadToEnd();
                    }

                    string audioPath = ExtractString(responseText, "audio_path");
                    if (string.IsNullOrEmpty(audioPath))
                    {
                        lock (queueLock)
                            _ttsErrorQueue.Enqueue(
                                "сервер вернул пустой audio_path для реплики: " +
                                text.Substring(0, Math.Min(80, text.Length))
                            );
                        continue;
                    }

                    lock (queueLock)
                        _ttsAudioPathQueue.Enqueue(audioPath);
                }
                catch (Exception e)
                {
                    lock (queueLock)
                        _ttsErrorQueue.Enqueue(e.Message);
                }
            }
        }

        private void LoadAndPlayAudio(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                byte[] bytes = File.ReadAllBytes(path);
                AudioClip clip = ParseWav(bytes);
                if (clip == null) return;
                if (audioSource != null) { audioSource.Stop(); audioSource.clip = clip; audioSource.Play(); }
                try { File.Delete(path); } catch { }
            }
            catch (Exception e) { ModConsole.Print("[V] audio: " + e.Message); }
        }

        private AudioClip ParseWav(byte[] bytes)
        {
            if (bytes.Length < 44) return null;
            if (bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F') return null;
            if (bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E') return null;
            int pos = 12, channels = 1, sampleRate = 48000, bitsPerSample = 16, dataOffset = -1, dataSize = 0;
            while (pos + 8 <= bytes.Length)
            {
                string cid = "" + (char)bytes[pos] + (char)bytes[pos + 1] + (char)bytes[pos + 2] + (char)bytes[pos + 3];
                int csz = BitConverter.ToInt32(bytes, pos + 4);
                if (cid == "fmt ") { channels = BitConverter.ToInt16(bytes, pos + 10); sampleRate = BitConverter.ToInt32(bytes, pos + 12); bitsPerSample = BitConverter.ToInt16(bytes, pos + 22); }
                else if (cid == "data") { dataOffset = pos + 8; dataSize = csz; break; }
                pos += 8 + csz; if (csz % 2 != 0) pos++;
            }
            if (dataOffset < 0 || dataSize <= 0) return null;
            int bps = bitsPerSample / 8;
            int totalSamples = dataSize / bps;
            int spc = totalSamples / channels;
            float[] samples = new float[totalSamples];
            for (int i = 0; i < totalSamples; i++)
            {
                int off = dataOffset + i * bps;
                if (off + bps > bytes.Length) break;
                if (bitsPerSample == 16) samples[i] = BitConverter.ToInt16(bytes, off) / 32768f;
                else if (bitsPerSample == 8) samples[i] = (bytes[off] - 128) / 128f;
                else if (bitsPerSample == 32) samples[i] = BitConverter.ToInt32(bytes, off) / 2147483648f;
            }
            var clip = AudioClip.Create("v_voice", spc, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static string F(float v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        private static string JsonQuote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append("\""); return sb.ToString();
        }
        private static string BuildJsonStringArray(List<string> items)
        {
            if (items == null || items.Count == 0) return "[]";
            var sb = new StringBuilder("[");
            for (int i = 0; i < items.Count; i++) { if (i > 0) sb.Append(","); sb.Append(JsonQuote(items[i])); }
            sb.Append("]"); return sb.ToString();
        }
        private static string ExtractString(string json, string key)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            return m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "").Replace("\\t", "\t");
        }

        private static string ExtractObject(string json, string key)
        {
            if (string.IsNullOrEmpty(json))
                return "";

            string pattern =
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\{";

            Match match = Regex.Match(json, pattern);

            if (!match.Success)
                return "";

            int start = match.Index + match.Length - 1;
            int depth = 0;
            bool inString = false;
            bool escaped = false;

            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (c == '\\')
                    {
                        escaped = true;
                        continue;
                    }

                    if (c == '"')
                        inString = false;

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '{')
                    depth++;

                else if (c == '}')
                {
                    depth--;

                    if (depth == 0)
                        return json.Substring(
                            start,
                            i - start + 1
                        );
                }
            }

            return "";
        }

        private static float ExtractFloatValue(
            string json,
            string key,
            float defaultValue)
        {
            string pattern =
                "\"" + Regex.Escape(key) +
                "\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?)";

            Match match = Regex.Match(json ?? "", pattern);

            if (!match.Success)
                return defaultValue;

            float value;

            if (float.TryParse(
                match.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value))
            {
                return value;
            }

            return defaultValue;
        }

        private static int ExtractIntValue(
            string json,
            string key,
            int defaultValue)
        {
            string pattern =
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)";

            Match match = Regex.Match(json ?? "", pattern);

            if (!match.Success)
                return defaultValue;

            int value;

            if (int.TryParse(match.Groups[1].Value, out value))
                return value;

            return defaultValue;
        }
        private void SetStatus(string msg, float timeout = 3f) { statusMessage = msg; statusTimeout = timeout; }
    }
}