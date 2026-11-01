using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using LiveRaid.Client.Bots;
using UnityEngine;

namespace LiveRaid.Client.Spectator
{
    /// <summary>
    /// Camera and keys after your death (on the GameWorld object, gone with the raid). Free flight: mouse looks, WASD moves,
    /// Space/E up, Ctrl/Q down, Shift faster, Alt slower, wheel changes the speed. Follow: the camera flies behind a bot and turns
    /// around it with the mouse. Take over: the bot in the middle of the screen (or the one you follow) becomes yours.
    /// The End raid key works in every mode and is always shown bottom left.
    /// </summary>
    internal class SpectatorCamera : MonoBehaviour
    {
        public static SpectatorCamera Instance { get; private set; }

        private const float MouseSpeed = 2f;
        private const float TagRange = 150f;

        private Vector3 _pos;
        private float _yaw;
        private float _pitch;
        private float _speed;
        private float _freeAt;
        private string _dyingText;
        private bool _ownCameraTaken;

        private Player _follow;
        private float _followDistance = 3.5f;

        // "Follow a random PMC at the start" (observer raids): looking for one until this time; 0 = done / not wanted
        private float _autoFollowUntil;
        private float _nextAutoFollow;

        private Player _target;
        private float _nextTargetScan;
        private readonly List<Player> _inView = new List<Player>();

        private string _toast;
        private float _toastUntil;

        // O - raid time (OO - with the exits), as in the game; your controls are off while the camera is loose, so the key is read here
        private float _lastTimerKeyAt = -9f;
        private float _ownTimerUntil;
        private bool _ownTimerExits;
        private bool _mouseBroken;

        private GUIStyle _keyStyle;
        private GUIStyle _hintStyle;
        private GUIStyle _tagStyle;
        private GUIStyle _bigStyle;
        private int _styleHeight;

        public static void Remove()
        {
            if (Instance != null)
            {
                Destroy(Instance);
            }

            Instance = null;
        }

        private static Camera Cam => CameraManager.Exist ? CameraManager.Instance.Camera : null;

        public void BeginDying(string text, float seconds = 2.5f)
        {
            Instance = this;
            _dyingText = text;
            _freeAt = Time.time + seconds;
            _follow = null;
            _autoFollowUntil = SpectatorSession.Observer && Settings.ObserverFollowAtStart.Value ? Time.time + 60f : 0f;
            SpectatorSession.State = SpectatorState.Dying;
            if (_speed <= 0f)
            {
                _speed = Settings.FreeCameraSpeed.Value;
            }
        }

        private void Update()
        {
            if (Instance != this)
            {
                return;
            }

            Diag.Guard("Spectator.Update", Tick);
        }

        private void Tick()
        {
            SpectatorState state = SpectatorSession.State;
            if (state == SpectatorState.Off || state == SpectatorState.Ending)
            {
                return;
            }

            if (Settings.SpectatorEndRaid.Value.IsDown())
            {
                SpectatorSession.EndRaid("End raid key");
                return;
            }

            if (state != SpectatorState.TakenOver)
            {
                Diag.Guard("Spectator.Timer", TimerKey);
            }

            switch (state)
            {
                case SpectatorState.Dying:
                    if (Time.time >= _freeAt)
                    {
                        StartFree();
                    }

                    break;

                case SpectatorState.FreeCam:
                case SpectatorState.Follow:
                    LockCursor();
                    if (state == SpectatorState.FreeCam)
                    {
                        Fly();
                    }
                    else
                    {
                        FollowInput();
                    }

                    if (Time.time >= _nextTargetScan)
                    {
                        _nextTargetScan = Time.time + 0.1f;
                        ScanView();
                    }

                    HandleBotKeys();
                    AutoFollowTick();
                    break;

                case SpectatorState.TakenOver:
                    if (!TakeOver.Starting && Settings.SpectatorLeaveBot.Value.IsDown())
                    {
                        TakeOver.Leave();
                        break;
                    }

                    TakeOver.Tick();
                    break;
            }
        }

        private void LateUpdate()
        {
            if (Instance != this)
            {
                return;
            }

            Diag.Guard("Spectator.Camera", PlaceCamera);
            Diag.Guard("Spectator.Listener", KeepListener);
            Diag.Guard("Spectator.ListenerRoom", KeepListenerRoom);
            Diag.Guard("Spectator.MyPlayer", KeepMyPlayer);
            if (_ownCameraTaken && Cam != null)
            {
                Diag.Guard("Spectator.Culling", () => CullingProxy.Tick(Cam.transform.position));
            }
        }

        /// <summary>
        /// The game shows the raid timer on O (and the exits on a double O) through your player's input, which is off while the camera is loose
        /// (after death, in an observer raid). The key is read here and the spectator HUD draws the time (and on a double O the exits).
        /// </summary>
        private void TimerKey()
        {
            if (!Input.GetKeyDown(KeyCode.O))
            {
                return;
            }

            // The game's own panel (GameUI.TimerPanel) reported itself as shown after the first O but drew nothing while the battle UI is hidden
            // around the loose camera (observer raid 14:21: no time on O). The spectator HUD always draws it itself.
            bool exits = Time.unscaledTime - _lastTimerKeyAt < 0.45f;
            _lastTimerKeyAt = Time.unscaledTime;
            _ownTimerUntil = Time.unscaledTime + 6f;
            _ownTimerExits = exits;
        }

        private void DrawOwnTimer()
        {
            if (Time.unscaledTime > _ownTimerUntil)
            {
                return;
            }

            float left = Mathf.Max(0f, SPT.SinglePlayer.Utils.InRaid.RaidTimeUtil.GetRemainingRaidSeconds());
            TimeSpan t = TimeSpan.FromSeconds(left);
            TimeSpan spent = TimeSpan.FromSeconds(Mathf.Max(0f, SPT.SinglePlayer.Utils.InRaid.RaidTimeUtil.GetSecondsSinceSpawning()));
            string text = (t.Hours > 0 ? $"{t.Hours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}") + Loc.T("  до конца рейда  ·  прошло ", "  left in the raid  ·  elapsed ") +
                          (spent.Hours > 0 ? $"{spent.Hours}:{spent.Minutes:00}:{spent.Seconds:00}" : $"{spent.Minutes:00}:{spent.Seconds:00}");
            if (_ownTimerExits)
            {
                text += "\n" + ExitsLine();
            }

            Vector2 size = _keyStyle.CalcSize(new GUIContent(text));
            float x = Screen.width - size.x - 24f;
            float y = 24f;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(x - 8f, y - 4f, size.x + 16f, size.y + 8f), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(x, y, size.x + 4f, size.y), text, _keyStyle);
        }

        /// <summary>Open exits of your side (names only), for a double O.</summary>
        private static string ExitsLine()
        {
            try
            {
                var controller = Singleton<GameWorld>.Instance?.ExfiltrationController;
                Player me = SpectatorSession.Original;
                if (controller == null || me == null)
                {
                    return "";
                }

                List<string> names = new List<string>();
                foreach (EFT.Interactive.ExfiltrationPoint point in controller.EligiblePoints(me.Profile))
                {
                    if (point != null && point.Status != EFT.Interactive.EExfiltrationStatus.NotPresent)
                    {
                        names.Add(point.Settings.Name.Localized());
                    }
                }

                return names.Count > 0 ? Loc.T("Выходы: ", "Exits: ") + string.Join(", ", names) : "";
            }
            catch
            {
                return "";
            }
        }

        private static readonly System.Reflection.FieldInfo MyPlayerField = HarmonyLib.AccessTools.Field(typeof(GamePlayerOwner), "_myPlayer");

        /// <summary>
        /// EFT's spatial sound works only while GamePlayerOwner.MyPlayer is set; destroying the taken bot's controls cleared it and the
        /// whole raid went silent and flat (no steps, no running). Without a body in hand it is your dead character again.
        /// </summary>
        private static void KeepMyPlayer()
        {
            if (GamePlayerOwner.MyPlayer == null && SpectatorSession.Original != null && MyPlayerField != null)
            {
                MyPlayerField.SetValue(null, SpectatorSession.Original);
            }
        }

        /// <summary>Where sounds are heard from while the camera is loose (for EFT's occlusion it is your player's body otherwise).</summary>
        public static bool ListenerOverride(out Vector3 position)
        {
            position = default;
            Camera cam = Cam;
            SpectatorState state = SpectatorSession.State;
            bool loose = state == SpectatorState.FreeCam || state == SpectatorState.Follow
                         || (state == SpectatorState.Dying && Instance != null && Instance._ownCameraTaken)
                         || (state == SpectatorState.TakenOver && TakeOver.Starting);
            if (!loose || cam == null)
            {
                return false;
            }

            position = cam.transform.position;
            return true;
        }

        private float _fadeFrom;
        private const float FadeSeconds = 0.4f;

        /// <summary>A short dip to black and back when the view jumps (taking a body, coming loose), instead of a hard cut.</summary>
        public void Fade() => _fadeFrom = Time.unscaledTime;

        private static readonly System.Reflection.FieldInfo ListenerTarget = HarmonyLib.AccessTools.Field(typeof(AudioListenerConsistencyManager), "_target");

        /// <summary>
        /// Destroying a player camera controller resets EFT's audio listener to (0, -1000, 0): after your death the whole map went quiet
        /// and muffled. The listener follows the camera again (CameraManager does that at raid start), whoever moves the camera.
        /// </summary>
        private static void KeepListener()
        {
            if (SpectatorSession.State == SpectatorState.Dying && !_cameraFreed())
            {
                return;
            }

            Camera cam = Cam;
            AudioListenerConsistencyManager listener = Singleton<AudioListenerConsistencyManager>.Instance;
            if (cam == null || listener == null || ListenerTarget == null)
            {
                return;
            }

            if (!ReferenceEquals(ListenerTarget.GetValue(listener) as Transform, cam.transform))
            {
                CameraManager.Instance.method_7();
            }
        }

        private static readonly System.Reflection.FieldInfo RoomStorageField = HarmonyLib.AccessTools.Field(typeof(Audio.SpatialSystem.SpatialAudioSystem), "_audioRoomStorage");
        private float _nextRoomCheck;
        private string _listenerRoomName;

        /// <summary>
        /// EFT's spatial sound muffles and routes every sound by the audio "room" the listener is in, and that room is set only when YOUR player's collider
        /// walks into a room volume - the loose camera flew through walls with the listener's room staying where the hidden body lay: a shot in the room
        /// the camera was in sounded like one from outside, and the ambience (wind, rain, echo) was the body's. The camera's room is looked up from its
        /// position and put in the listener's place. (The position used for the occlusion rays follows the camera already - SpectatorListenerPatch.)
        /// </summary>
        private void KeepListenerRoom()
        {
            if (Time.unscaledTime < _nextRoomCheck || !ListenerOverride(out Vector3 pos) || RoomStorageField == null)
            {
                return;
            }

            _nextRoomCheck = Time.unscaledTime + 0.15f;
            var system = MonoBehaviourSingleton<Audio.SpatialSystem.SpatialAudioSystem>.Instance;
            var storage = system != null ? RoomStorageField.GetValue(system) as Audio.SpatialSystem.AudioRoomStorage : null;
            if (storage == null || storage._roomOctree == null)
            {
                return;
            }

            Audio.SpatialSystem.ISpatialAudioRoom current = storage.ListenerCurrentRoom;
            Audio.SpatialSystem.ISpatialAudioRoom room = storage.FindActualCurrentRoom(current, pos);
            if (room == null || !room.IsValid || (current != null && current.IsValid && current.ID == room.ID))
            {
                return;
            }

            storage.list_3.Clear();
            storage.list_4.Clear();
            if (room is global::SpatialAudioRoom exact)
            {
                storage.AddListenerCurrentRoom(exact);
                if (!exact.IsOutdoor)
                {
                    // Indoors the outdoor volume around the building counts as well (the sound system looks at both)
                    foreach (global::SpatialAudioRoom outdoor in storage._outdoorAudioRooms)
                    {
                        if (storage.CheckPositionInsideRoom(outdoor, pos))
                        {
                            storage.AddListenerCurrentRoom(outdoor);
                            break;
                        }
                    }
                }
            }

            string name = room.ID.ToString();
            if (name != _listenerRoomName)
            {
                _listenerRoomName = name;
                Log.Detail("debug", $"LR Spectator: the camera is in sound room {name} ({(room.IsOutdoor ? "outdoors" : "indoors")}) - muffling and ambience follow it");
            }
        }

        private static Func<bool> _cameraFreed = () => Instance != null && Instance._ownCameraTaken;

        /// <summary>Back to free flight (a take-over that did not work out).</summary>
        public void ResumeFree() => StartFree();

        /// <summary>Free flight from where the camera is now (your head, or the head of the bot that just died).</summary>
        private void StartFree()
        {
            Camera cam = Cam;
            if (cam == null)
            {
                return;
            }

            if (!_ownCameraTaken)
            {
                // From now on LiveRaid moves the camera; your body is seen from outside (first person draws only its shadow)
                _ownCameraTaken = true;
                Player me = SpectatorSession.Original;
                PlayerCameraController.Destroy(me);
                try
                {
                    me.PointOfView = EPointOfView.ThirdPerson;
                }
                catch (Exception e)
                {
                    Log.Detail("debug", "LR Spectator: third person on the body: " + e.Message);
                }

                SwitchOffGoggles();
            }

            ClearDeathEffects();
            Fade();

            _pos = cam.transform.position;
            Vector3 euler = cam.transform.rotation.eulerAngles;
            _yaw = euler.y;
            _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
            _follow = null;
            SpectatorSession.State = SpectatorState.FreeCam;
        }

        private static readonly System.Reflection.FieldInfo DeathFadeField = HarmonyLib.AccessTools.Field(typeof(EffectsController), "_deathFade");

        /// <summary>The "closing eyes" fade on the camera, in case EFT started it before LiveRaid could stop it.</summary>
        public static void ClearDeathEffects()
        {
            try
            {
                foreach (EffectsController effects in FindObjectsOfType<EffectsController>())
                {
                    if (DeathFadeField?.GetValue(effects) is DeathFade fade)
                    {
                        fade.DisableEffect();
                    }
                }
            }
            catch (Exception e)
            {
                Log.Detail("debug", "LR Spectator: death fade: " + e.Message);
            }
        }

        private static void SwitchOffGoggles()
        {
            try
            {
                CameraManager manager = CameraManager.Instance;
                if (manager.NightVision != null && manager.NightVision.On)
                {
                    manager.NightVision.StartSwitch(false);
                }

                if (manager.ThermalVision != null && manager.ThermalVision.On)
                {
                    manager.ThermalVision.StartSwitch(false);
                }
            }
            catch (Exception e)
            {
                Log.Detail("debug", "LR Spectator: goggles: " + e.Message);
            }
        }

        private static void LockCursor()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private Vector2 MouseDelta()
        {
            if (_mouseBroken)
            {
                return Vector2.zero;
            }

            try
            {
                return new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * MouseSpeed;
            }
            catch (Exception e)
            {
                _mouseBroken = true;
                Log.Warn("LR Spectator: no mouse axes in this game build, the free camera turns with the arrow keys: " + e.Message);
                return Vector2.zero;
            }
        }

        private void Look()
        {
            Vector2 mouse = MouseDelta();
            float keys = 90f * Time.unscaledDeltaTime;
            if (Input.GetKey(KeyCode.LeftArrow)) mouse.x -= keys;
            if (Input.GetKey(KeyCode.RightArrow)) mouse.x += keys;
            if (Input.GetKey(KeyCode.UpArrow)) mouse.y += keys;
            if (Input.GetKey(KeyCode.DownArrow)) mouse.y -= keys;
            _yaw += mouse.x;
            _pitch = Mathf.Clamp(_pitch - mouse.y, -89f, 89f);
        }

        private void Fly()
        {
            Look();
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f)
            {
                _speed = Mathf.Clamp(_speed * (wheel > 0 ? 1.25f : 0.8f), 0.5f, 120f);
            }

            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 move = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) move += rot * Vector3.forward;
            if (Input.GetKey(KeyCode.S)) move -= rot * Vector3.forward;
            if (Input.GetKey(KeyCode.D)) move += rot * Vector3.right;
            if (Input.GetKey(KeyCode.A)) move -= rot * Vector3.right;
            if (Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.E)) move += Vector3.up;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.Q)) move -= Vector3.up;

            float speed = _speed;
            if (Input.GetKey(KeyCode.LeftShift)) speed *= 4f;
            if (Input.GetKey(KeyCode.LeftAlt)) speed *= 0.25f;
            _pos += move.normalized * speed * Time.unscaledDeltaTime;
        }

        private void FollowInput()
        {
            if (_follow == null || _follow.HealthController?.IsAlive != true)
            {
                Toast(_follow != null ? Loc.T($"{_follow.Profile.Nickname} погиб", $"{_follow.Profile.Nickname} died") : Loc.T("Бот пропал", "The bot is gone"));
                LeaveFollow();
                return;
            }

            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D))
            {
                LeaveFollow();
                return;
            }

            if (Settings.SpectatorToggleView.Value.IsDown())
            {
                _followEyes = !_followEyes;
                _eyesRot = Cam != null ? Cam.transform.rotation : Quaternion.identity;
                Fade();
                Toast(_followEyes ? Loc.T($"Глазами {_follow.Profile.Nickname}", $"Through the eyes of {_follow.Profile.Nickname}") : Loc.T("Камера за ботом", "Camera behind the bot"));
            }

            if (_followEyes)
            {
                return;
            }

            Look();
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f)
            {
                _followDistance = Mathf.Clamp(_followDistance * (wheel > 0 ? 0.85f : 1.18f), 1.2f, 25f);
            }
        }

        private void LeaveFollow()
        {
            Camera cam = Cam;
            if (cam != null)
            {
                _pos = cam.transform.position;
            }

            _follow = null;
            ShowHead();
            SpectatorSession.State = SpectatorState.FreeCam;
        }

        private void HandleBotKeys()
        {
            if (Settings.SpectatorNextBot.Value.IsDown())
            {
                FollowNext();
                return;
            }

            Player pick = SpectatorSession.State == SpectatorState.Follow ? _follow : _target;
            if (pick == null)
            {
                return;
            }

            if (Settings.SpectatorFollow.Value.IsDown() && SpectatorSession.State == SpectatorState.FreeCam)
            {
                StartFollow(pick);
            }
            else if (Settings.SpectatorTakeOver.Value.IsDown())
            {
                if (!Settings.TakeOverBots.Value)
                {
                    Toast(Loc.T("Вселение выключено в F12 (LiveRaid → 11)", "Taking over is off in F12 (LiveRaid → 11)"));
                }
                else if (TakeOver.Start(pick, out string error))
                {
                    _follow = null;
                    _target = null;
                    Toast(Loc.T($"Вселение (сырой режим): {pick.Profile.Nickname}", $"Taking over (raw mode): {pick.Profile.Nickname}"));
                }
                else
                {
                    Toast(Loc.T("Вселиться нельзя: ", "Cannot take over: ") + error);
                }
            }
        }

        /// <summary>
        /// Observer raid, first minute: the camera jumps to a random PMC (as if V were pressed on it) and sits behind it. PMCs come in a few seconds after
        /// the start, so it is asked twice a second until one is there; free flight (WASD) or another bot picked by hand ends the search.
        /// </summary>
        private void AutoFollowTick()
        {
            if (_autoFollowUntil <= 0f)
            {
                return;
            }

            if (SpectatorSession.State != SpectatorState.FreeCam || Time.time > _autoFollowUntil || !Settings.ObserverFollowAtStart.Value
                || Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D))
            {
                if (Time.time > _autoFollowUntil && SpectatorSession.State == SpectatorState.FreeCam)
                {
                    Log.Info("LR Spectator: no PMC in the raid in the first minute - the camera stays free");
                }

                _autoFollowUntil = 0f;
                return;
            }

            if (Time.time < _nextAutoFollow)
            {
                return;
            }

            _nextAutoFollow = Time.time + 0.5f;
            List<Player> pmcs = AliveBots().Where(p => BotProfiles.KindOf(p.AIData.BotOwner) == BotKind.Pmc).ToList();
            if (pmcs.Count == 0)
            {
                return;
            }

            Player pick = pmcs[UnityEngine.Random.Range(0, pmcs.Count)];
            _autoFollowUntil = 0f;
            Fade();
            StartFollow(pick);

            // Behind the bot, looking where it looks (V on a bot in view keeps the camera's own heading)
            Vector3 look = pick.LookDirection;
            look.y = 0f;
            if (look.sqrMagnitude > 0.01f)
            {
                _yaw = Quaternion.LookRotation(look).eulerAngles.y;
            }

            _pitch = 12f;
            Toast(Loc.T($"Слежение: {pick.Profile.Nickname}", $"Following: {pick.Profile.Nickname}"));
            Log.Info($"LR Spectator: observer raid starts by following a random PMC - {pick.Profile.Nickname} ({pmcs.Count} PMCs in the raid)");
        }

        private void StartFollow(Player bot)
        {
            _follow = bot;
            Vector3 euler = (Cam != null ? Cam.transform.rotation : Quaternion.identity).eulerAngles;
            _yaw = euler.y;
            _pitch = Mathf.Clamp(euler.x > 180f ? euler.x - 360f : euler.x, -30f, 60f);
            SpectatorSession.State = SpectatorState.Follow;
        }

        private void FollowNext()
        {
            Camera cam = Cam;
            if (cam == null)
            {
                return;
            }

            Vector3 from = cam.transform.position;
            List<Player> bots = AliveBots().OrderBy(p => (p.Position - from).sqrMagnitude).ToList();
            if (bots.Count == 0)
            {
                Toast(Loc.T("Живых ботов нет", "No living bots"));
                return;
            }

            // Nearest first; pressing again walks further out from the bot you follow now
            int at = _follow != null ? bots.IndexOf(_follow) : -1;
            StartFollow(bots[(at + 1) % bots.Count]);
        }

        private static IEnumerable<Player> AliveBots()
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                yield break;
            }

            foreach (Player player in world.AllAlivePlayersList)
            {
                if (TakeOver.IsWatchable(player))
                {
                    yield return player;
                }
            }
        }

        /// <summary>Bots in view for the name tags, and the one in the middle of the screen.</summary>
        private void ScanView()
        {
            Camera cam = Cam;
            _inView.Clear();
            _target = null;
            if (cam == null)
            {
                return;
            }

            Vector3 eye = cam.transform.position;
            Vector3 forward = cam.transform.forward;
            float best = float.MaxValue;
            foreach (Player bot in AliveBots())
            {
                Vector3 to = Head(bot) - eye;
                float dist = to.magnitude;
                if (dist < 0.5f || dist > 500f)
                {
                    continue;
                }

                float angle = Vector3.Angle(forward, to);
                if (dist <= TagRange && angle < cam.fieldOfView)
                {
                    _inView.Add(bot);
                }

                // In the middle: a few degrees, or within a body width when close
                float allowed = Mathf.Max(3f, Mathf.Atan2(0.9f, dist) * Mathf.Rad2Deg);
                if (angle <= allowed && angle + dist * 0.002f < best)
                {
                    best = angle + dist * 0.002f;
                    _target = bot;
                }
            }
        }

        private static Vector3 Head(Player player) =>
            player.PlayerBones?.Head != null ? player.PlayerBones.Head.Original.position : player.Position + Vector3.up * 1.55f;

        private void PlaceCamera()
        {
            Camera cam = Cam;
            if (cam == null)
            {
                return;
            }

            switch (SpectatorSession.State)
            {
                case SpectatorState.FreeCam:
                    cam.transform.SetPositionAndRotation(_pos, Quaternion.Euler(_pitch, _yaw, 0f));
                    break;

                case SpectatorState.Follow when _follow != null:
                    if (_followEyes)
                    {
                        Eyes(cam, _follow);
                    }
                    else
                    {
                        ShowHead();
                        Orbit(cam, Head(_follow) + Vector3.up * 0.15f, Quaternion.Euler(_pitch, _yaw, 0f), _followDistance, 0.35f);
                    }

                    break;

                default:
                    ShowHead();
                    break;
            }
        }

        // ---------------- watching through a bot's eyes ----------------

        private bool _followEyes;
        private Quaternion _eyesRot = Quaternion.identity;
        private Player _hiddenOf;
        private readonly List<(Renderer renderer, UnityEngine.Rendering.ShadowCastingMode mode)> _hidden = new List<(Renderer, UnityEngine.Rendering.ShadowCastingMode)>();

        private static readonly EFT.InventoryLogic.EquipmentSlot[] HeadSlots =
        {
            EFT.InventoryLogic.EquipmentSlot.Headwear, EFT.InventoryLogic.EquipmentSlot.Earpiece,
            EFT.InventoryLogic.EquipmentSlot.FaceCover, EFT.InventoryLogic.EquipmentSlot.Eyewear,
        };

        /// <summary>The camera in the bot's head, turning (smoothly) where the bot looks; its head and headgear draw only their shadow.</summary>
        private void Eyes(Camera cam, Player bot)
        {
            Transform eye = bot.CameraPosition;
            Vector3 pos = eye != null ? eye.position : Head(bot);
            Vector3 look = bot.LookDirection;
            Quaternion want = look.sqrMagnitude > 0.001f ? Quaternion.LookRotation(look) : _eyesRot;
            _eyesRot = Quaternion.Slerp(_eyesRot, want, 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime));
            cam.transform.SetPositionAndRotation(pos + _eyesRot * Vector3.forward * 0.1f, _eyesRot);
            HideHead(bot);
        }

        private void HideHead(Player bot)
        {
            if (_hiddenOf == bot)
            {
                return;
            }

            ShowHead();
            _hiddenOf = bot;
            try
            {
                List<Renderer> renderers = new List<Renderer>();
                if (bot.PlayerBody.BodySkins.TryGetValue(EBodyModelPart.Head, out var head))
                {
                    renderers.AddRange(head.GetRenderers());
                }

                foreach (EFT.InventoryLogic.EquipmentSlot slot in HeadSlots)
                {
                    GameObject model = bot.PlayerBody.SlotViews.GetByKey(slot)?.ParentedModel?.Value;
                    if (model != null)
                    {
                        renderers.AddRange(model.GetComponentsInChildren<Renderer>(true));
                    }
                }

                foreach (Renderer r in renderers)
                {
                    if (r != null)
                    {
                        _hidden.Add((r, r.shadowCastingMode));
                        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Detail("debug", "LR Spectator: hiding the head: " + e.Message);
            }
        }

        private void ShowHead()
        {
            foreach ((Renderer renderer, UnityEngine.Rendering.ShadowCastingMode mode) in _hidden)
            {
                if (renderer != null)
                {
                    renderer.shadowCastingMode = mode;
                }
            }

            _hidden.Clear();
            _hiddenOf = null;
        }

        private static void Orbit(Camera cam, Vector3 pivot, Quaternion rot, float distance, float shoulder)
        {
            Vector3 wanted = pivot - rot * Vector3.forward * distance + rot * Vector3.right * shoulder;
            Vector3 dir = wanted - pivot;
            float length = dir.magnitude;
            if (length > 0.01f && Physics.SphereCast(pivot, 0.2f, dir / length, out RaycastHit hit, length, LayersMaskController.HighPolyWithTerrainMask))
            {
                wanted = pivot + dir / length * Mathf.Max(0.3f, hit.distance - 0.1f);
            }

            cam.transform.SetPositionAndRotation(wanted, rot);
        }

        private void Toast(string text)
        {
            _toast = text;
            _toastUntil = Time.time + 3f;
        }

        // ---------------- HUD ----------------

        private void OnGUI()
        {
            if (Instance != this || SpectatorSession.State == SpectatorState.Off || SpectatorSession.State == SpectatorState.Ending)
            {
                return;
            }

            try
            {
                DrawHud();
                float fade = (Time.unscaledTime - _fadeFrom) / FadeSeconds;
                if (fade < 1f)
                {
                    GUI.color = new Color(0f, 0f, 0f, 1f - fade);
                    GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
                    GUI.color = Color.white;
                }
            }
            catch (Exception e)
            {
                Diag.Report("Spectator.Hud", e);
            }
        }

        private void MakeStyles()
        {
            if (_keyStyle != null && _styleHeight == Screen.height)
            {
                return;
            }

            _styleHeight = Screen.height;
            float k = Mathf.Max(0.7f, Screen.height / 1080f);
            _keyStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(14 * k), fontStyle = FontStyle.Bold, richText = true };
            _keyStyle.normal.textColor = new Color(1f, 0.84f, 0.35f);
            _hintStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(12 * k), richText = true, wordWrap = false };
            _hintStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f, 0.85f);
            _tagStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(11 * k), alignment = TextAnchor.LowerCenter, richText = true };
            _tagStyle.normal.textColor = Color.white;
            _bigStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(22 * k), alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, richText = true };
            _bigStyle.normal.textColor = new Color(0.95f, 0.9f, 0.85f);
        }

        private void DrawHud()
        {
            MakeStyles();
            SpectatorState state = SpectatorSession.State;
            float line = _hintStyle.fontSize * 1.6f;
            float x = 18f;
            // Above SPT's version line in the corner
            float y = Screen.height - 18f - _keyStyle.fontSize * 1.8f - _hintStyle.fontSize * 2.4f;

            // Bottom left, always: the way out of the raid
            string endKey = Settings.SpectatorEndRaid.Value.ToString();
            string endText = SpectatorSession.Observer ? Loc.T($"[{endKey}]  Закончить рейд (наблюдение, не засчитывается)", $"[{endKey}]  End the raid (observing, not counted)") : Loc.T($"[{endKey}]  Закончить рейд", $"[{endKey}]  End the raid");
            Vector2 size = _keyStyle.CalcSize(new GUIContent(endText));
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(x - 8f, y - 4f, size.x + 16f, size.y + 8f), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(x, y, size.x + 4f, size.y), endText, _keyStyle);

            string hint = Hint(state);
            if (!string.IsNullOrEmpty(hint))
            {
                GUI.Label(new Rect(x, y - line - 6f, Screen.width - 2 * x, line), hint, _hintStyle);
            }

            DrawOwnTimer();
            if (state == SpectatorState.Dying && !string.IsNullOrEmpty(_dyingText))
            {
                GUI.Label(new Rect(0, Screen.height * 0.28f, Screen.width, 40f), _dyingText, _bigStyle);
            }

            if (state == SpectatorState.FreeCam || state == SpectatorState.Follow)
            {
                DrawTags();
            }

            if (state == SpectatorState.TakenOver)
            {
                if (TakeOver.Exit != null)
                {
                    GUI.Label(new Rect(0, Screen.height * 0.2f, Screen.width, 40f),
                        Loc.T($"Выход «{TakeOver.Exit.Settings.Name.Localized()}»: {Mathf.CeilToInt(TakeOver.ExitLeft)} с", $"Exit \"{TakeOver.Exit.Settings.Name.Localized()}\": {Mathf.CeilToInt(TakeOver.ExitLeft)} s"), _bigStyle);
                }

            }

            if (_toast != null && Time.time < _toastUntil)
            {
                GUI.Label(new Rect(0, Screen.height * 0.34f, Screen.width, 40f), _toast, _bigStyle);
            }
        }

        private string Hint(SpectatorState state)
        {
            string take = Settings.TakeOverBots.Value ? $"{Settings.SpectatorTakeOver.Value} — " + Loc.T("вселиться", "take over") + " · " : "";
            switch (state)
            {
                case SpectatorState.FreeCam:
                    return Loc.Ru
                        ? $"Мышь, WASD — полёт · Space/Ctrl — вверх/вниз · Shift — быстрее · колесо — скорость ({_speed:0} м/с) · " +
                          $"{take}{Settings.SpectatorFollow.Value} — следить · {Settings.SpectatorNextBot.Value} — следующий бот · O — время рейда"
                        : $"Mouse, WASD — fly · Space/Ctrl — up/down · Shift — faster · wheel — speed ({_speed:0} m/s) · " +
                          $"{take}{Settings.SpectatorFollow.Value} — follow · {Settings.SpectatorNextBot.Value} — next bot · O — raid time";
                case SpectatorState.Follow:
                    return Loc.Ru
                        ? $"Следите за <b>{_follow?.Profile?.Nickname}</b> ({Describe(_follow)}) · " +
                          (_followEyes ? "" : "мышь — вокруг · колесо — ближе/дальше · ") +
                          $"{Settings.SpectatorToggleView.Value} — {(_followEyes ? "камера за ботом" : "его глазами")} · " +
                          $"{take}{Settings.SpectatorNextBot.Value} — следующий · WASD — свободный полёт"
                        : $"Following <b>{_follow?.Profile?.Nickname}</b> ({Describe(_follow)}) · " +
                          (_followEyes ? "" : "mouse — around · wheel — closer/farther · ") +
                          $"{Settings.SpectatorToggleView.Value} — {(_followEyes ? "camera behind the bot" : "its eyes")} · " +
                          $"{take}{Settings.SpectatorNextBot.Value} — next · WASD — free flight";
                case SpectatorState.TakenOver when TakeOver.Starting:
                    return Loc.T($"Вселение в <b>{TakeOver.Current?.Profile?.Nickname}</b>…", $"Taking over <b>{TakeOver.Current?.Profile?.Nickname}</b>…");
                case SpectatorState.TakenOver:
                    return Loc.Ru
                        ? $"Вы играете за <b>{TakeOver.Current?.Profile?.Nickname}</b> ({Settings.SpectatorLeaveBot.Value} — выйти из бота) — в профиль не засчитывается" +
                          (SpectatorSession.Observer ? " (рейд-наблюдение не засчитывается вовсе)" : ", итог рейда: ваша смерть")
                        : $"You play <b>{TakeOver.Current?.Profile?.Nickname}</b> ({Settings.SpectatorLeaveBot.Value} — leave the bot) — not counted in the profile" +
                          (SpectatorSession.Observer ? " (an observer raid is not counted at all)" : ", the raid's outcome: your death");
                default:
                    return null;
            }
        }

        private void DrawTags()
        {
            Camera cam = Cam;
            if (cam == null)
            {
                return;
            }

            // The camera may render at a scaled resolution (DLSS/FSR): map its pixels to the screen
            float sx = Screen.width / (float)Mathf.Max(1, cam.pixelWidth);
            float sy = Screen.height / (float)Mathf.Max(1, cam.pixelHeight);
            foreach (Player bot in _inView.Concat(_target != null && !_inView.Contains(_target) ? new[] { _target } : Array.Empty<Player>()))
            {
                if (bot == null || bot.HealthController?.IsAlive != true || bot == _follow)
                {
                    continue;
                }

                bool isTarget = bot == _target;
                if (!isTarget && !Settings.SpectatorBotNames.Value)
                {
                    continue;
                }

                Vector3 screen = cam.WorldToScreenPoint(Head(bot) + Vector3.up * 0.35f);
                if (screen.z <= 0f)
                {
                    continue;
                }

                float px = screen.x * sx;
                float py = Screen.height - screen.y * sy;
                string text = isTarget
                    ? $"<b>{bot.Profile.Nickname}</b>\n{Describe(bot)} · {screen.z:0} " + Loc.T("м", "m") + "\n" + TargetKeys(bot)
                    : bot.Profile.Nickname;
                GUI.color = isTarget ? new Color(1f, 0.85f, 0.4f) : TagColor(bot);
                GUI.Label(new Rect(px - 150f, py - 60f, 300f, 60f), text, _tagStyle);
            }

            GUI.color = Color.white;
        }

        private static string TargetKeys(Player bot)
        {
            string follow = $"[{Settings.SpectatorFollow.Value}] " + Loc.T("следить", "follow");
            if (!Settings.TakeOverBots.Value)
            {
                return follow;
            }

            string refusal = TakeOver.Refusal(bot);
            return refusal == null ? $"[{Settings.SpectatorTakeOver.Value}] " + Loc.T("вселиться", "take over") + $"  {follow}" : $"{follow} · {refusal}";
        }

        private static string Describe(Player bot)
        {
            BotOwner owner = bot?.AIData?.BotOwner;
            if (owner == null)
            {
                return "";
            }

            switch (BotProfiles.KindOf(owner))
            {
                case BotKind.Pmc: return Loc.T("ЧВК ", "PMC ") + bot.Profile.Info.Side;
                case BotKind.Scav: return Loc.T("дикий", "scav");
                case BotKind.PlayerScav: return Loc.T("дикий-игрок", "player scav");
                default: return owner.Profile.Info.Settings.Role.ToString();
            }
        }

        private static Color TagColor(Player bot)
        {
            BotOwner owner = bot.AIData?.BotOwner;
            switch (owner != null ? BotProfiles.KindOf(owner) : BotKind.Other)
            {
                case BotKind.Pmc: return new Color(1f, 0.7f, 0.55f, 0.9f);
                case BotKind.Scav:
                case BotKind.PlayerScav: return new Color(0.8f, 0.9f, 0.75f, 0.85f);
                default: return new Color(1f, 0.45f, 0.45f, 0.9f);
            }
        }

        private void OnDestroy()
        {
            ShowHead();
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
