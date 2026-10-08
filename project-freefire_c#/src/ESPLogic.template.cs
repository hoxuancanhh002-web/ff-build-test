using System;
using System.Collections;
using System.IO;
using COW;
using COW.GamePlay;
using UnityEngine;

namespace ProjectEspPatch
{
    public static class Logic
    {
        private const int EspMaster = 1;
        private const int EspBox = 2;
        private const int EspTracer = 4;
        private const int EspHealth = 8;
        private const int EspName = 16;
        private const int EspDistance = 32;
        private const int EspFov = 64;
        private const int EspMask = 127;
        private const int EspCount = 256;
        private const int EspColorEnabled = 512;
        private const int EspSkeleton = 1024;

        private const int StateInitialized = 128;
        private const int AimEnabled = 32768;
        private const int AimModeShift = 16;
        private const int AimModeMask = 196608;
        private const int NoRecoil = 262144;
        private const int HeadRateShift = 19;
        private const int HeadRateMask = 3670016;
        private const int AimSystemEnabled = 4194304;
        private const int AimFovHide = 8388608;
        private const int AimSkipDowned = 1 << 11; // bit 11 (2048) — must stay within 0xFFFFFF mask
        private const int AuxFastParachute = 1;
        private const int AuxSpeedRunning = 2;
        private const int AuxSpeedRunningApplied = 4;
        private const int AuxFakeDamage = 8;
        private const int AuxWideCamera = 1 << 20;
        private const int AuxWideCamFovShift = 21;
        private const int AuxFastHeal = 1 << 27;
        private const int AuxFastFire = 1 << 28;
        private const int AuxBackJump  = 1 << 29; // bit 29: backjump (no fall gravity)
        private const int AuxGhostControl = 1 << 30; // bit 30: ghost sync-freeze control
        private const int AimKillEnabled   = 1 << 24; // bit 24: send TakeDamage msg 106 to closest enemy
        private const int EspWeapon        = 1 << 27; // bit 27: weapon name ESP
        private const int GhostScaleShift = 12;      // bits 12-14 in mainState: ghost button scale index (0=100%)

        private const int R8NoFog        = 1;   // r8 bit 0
        private const int R8FastCrouch   = 2;   // r8 bit 1
        private const int R8FastRevive   = 4;   // r8 bit 2
        private const int R8SkillCD      = 8;   // r8 bit 3
        private const int R8Chams        = 16;  // r8 bit 4
        private const int R8FastLoot     = 32;  // r8 bit 5
        private const int R8CamHack      = 64;  // r8 bit 6
        private const int R8UnlockFps    = 128; // r8 bit 7
        private const int AuxMask = 4095;
        private const int AuxFovRadiusShift = 4;
        private const int AuxSilentFovShift = 12;
        private const float AuxStateMarker = 1000000f;
        private const ulong SpeedRunningKey = 4995421289296778564UL;

        // Default: aimMode=2 (MIXED), headRate=3 (75%), AimEnabled off by default
        private const int DefaultStateBits = (2 << AimModeShift) | (3 << HeadRateShift);



        public static bool Bootstrap(Player self)
        {
            if (self == null)
            {
                return false;
            }
            bool stealthState;
            try
            {
                stealthState = self.IsInStealth();
                GameObject driver = GameObject.Find("__esp_driver");
                bool createdDriver = false;
                if (driver == null)
                {
                    Camera camera = Camera.main;
                    Transform legacyFov = camera == null
                        ? null
                        : camera.transform.Find("__esp_fov");
                    if (legacyFov != null)
                    {
                        UnityEngine.Object.Destroy(legacyFov.gameObject);
                    }
                    driver = new GameObject("__esp_driver");
                    createdDriver = true;
                }

                Transform oldLogHolder = driver.transform.Find("__esp_log_holder");
                if (oldLogHolder != null) UnityEngine.Object.Destroy(oldLogHolder.gameObject);

                Component component = driver.GetComponent(typeof(SceneEditBoxSelectTool));
                if (component == null)
                {
                    component = driver.AddComponent(typeof(SceneEditBoxSelectTool));
                }
                if (component == null)
                {
                    if (createdDriver)
                    {
                        UnityEngine.Object.Destroy(driver);
                    }
                }
                else
                {
                    if (createdDriver)
                    {
                        UnityEngine.Object.DontDestroyOnLoad(driver);
                        SceneEditBoxSelectTool menu = (SceneEditBoxSelectTool)component;
                        // Use SCENE_POSITION_FIELD.x as the last-read timestamp.
                        // -999f forces an immediate config read on the first Draw tick.
                        menu.{{SCENE_POSITION_FIELD}} = new Vector2(-999f, 0f);
                        int initialState = StateInitialized | EspMask | EspCount | DefaultStateBits;
                        menu.{{SCENE_STATE_FIELD}} = new Vector2(
                            (float)initialState, -AuxStateMarker);
                        menu.{{SCENE_MENU_FIELD}} = false;
                        // New game session — clear session guard, ping counter, and last-seen byte39.
                        PlayerPrefs.SetFloat("esp_sg", 0f);
                        PlayerPrefs.SetFloat("esp_pn", 0f);
                        PlayerPrefs.SetFloat("esp_p39", -1f);
                    }
                }
            }
            catch (Exception)
            {
                stealthState = false;
            }
            return stealthState;
        }

        public static void Draw(SceneEditBoxSelectTool self)
        {
            if (self == null)
            {
                return;
            }
            Event currentEvent = Event.current;
            if (currentEvent == null)
            {
                return;
            }
            if (currentEvent.type != EventType.Repaint)
            {
                return;
            }

            GameObject driverObject = self.gameObject;
            if (driverObject == null || driverObject.name != "__esp_driver")
            {
                return;
            }

            int screenWidth = Screen.width;
            int screenHeight = Screen.height;

            int state = (int)self.{{SCENE_STATE_FIELD}}.x;

            if ((state & StateInitialized) == 0)
            {
                // Force an immediate config read on the next interval check.
                self.{{SCENE_POSITION_FIELD}} = new Vector2(-999f, 0f);
                state = StateInitialized | EspMask | EspCount | DefaultStateBits;
                self.{{SCENE_STATE_FIELD}} = new Vector2(
                    (float)(state & 0xFFFFFF), -AuxStateMarker);
            }

            // Periodically refresh state from the .pdata config file (~1 second interval).
            // SCENE_POSITION_FIELD.x stores the timestamp of the last successful read.
            float now = Time.unscaledTime;
            int curFrame = Time.frameCount;
            if (curFrame % 1200 == 1)
            {
                System.GC.Collect();
                System.GC.Collect();
            }
            Player _localSelf = GameFacade.CurrentLocalPlayer();
            if (_localSelf != null && _localSelf.CurHP <= 0 && curFrame % 1200 == 1)
            {
                System.GC.Collect();
                System.GC.Collect();
            }
            if (now - self.{{SCENE_POSITION_FIELD}}.x > 1.0f)
            {
                self.{{SCENE_POSITION_FIELD}} = new Vector2(now, 0f);
                try
                {
                    string cfgPath = Application.persistentDataPath + "/contentcache/Compulsory/ios/gameassetbundles/ingame/.pdata";
                    if (File.Exists(cfgPath))
                    {
                        byte[] cfgBytes = File.ReadAllBytes(cfgPath);
                        // Liveness: byte 39 is a ping counter incremented every 5s by the iOS app.
                        // If it hasn’t changed in 15s and no session is active, features are disabled.
                        int _ping39 = cfgBytes.Length >= 40 ? cfgBytes[39] : 0;
                        int _lastPing = (int)PlayerPrefs.GetFloat("esp_p39", -1f);
                        if (_ping39 != _lastPing)
                        {
                            PlayerPrefs.SetFloat("esp_p39", (float)_ping39);
                            PlayerPrefs.SetFloat("esp_p39t", now);
                            // Count distinct byte-39 changes this session.
                            // A stolen .pdata only ever causes 1 change (-1→X); a live app causes 2+.
                            float _pn = PlayerPrefs.GetFloat("esp_pn", 0f) + 1f;
                            PlayerPrefs.SetFloat("esp_pn", _pn);
                        }
                        float _lastPingTime = PlayerPrefs.GetFloat("esp_p39t", now);
                        if (now - _lastPingTime > 15f)
                        {
                            // Ping stale (iOS app suspended/killed).
                            // If session was already validated this game, keep features running.
                            if (PlayerPrefs.GetFloat("esp_sg", 0f) < 0.5f && PlayerPrefs.GetFloat("esp_kv", 0f) < 0.5f)
                            {
                                PlayerPrefs.SetFloat("esp_tv", 0.0f);
                                state = StateInitialized;
                                self.{{SCENE_STATE_FIELD}} = new Vector2((float)(state & 0xFFFFFF), -AuxStateMarker);
                            }
                        }
                        else
                        {
                        // Features only activate after byte 39 has changed at least twice this session.
                        // First change: cold read (re-entry or stolen file). Second+: app is actually running.
                        if (PlayerPrefs.GetFloat("esp_pn", 0f) >= 2f)
                        {
                        PlayerPrefs.SetFloat("esp_sg", 1f);
                        PlayerPrefs.SetFloat("esp_tv", 1.0f);
                        int newState = StateInitialized | EspMask | EspCount | DefaultStateBits;
                        int newAux = 0;
                        if (cfgBytes.Length >= 4)
                        {
                            newState = (int)cfgBytes[0]
                                | ((int)cfgBytes[1] << 8)
                                | ((int)cfgBytes[2] << 16)
                                | ((int)cfgBytes[3] << 24);
                            newState |= StateInitialized;
                        }
                        if (cfgBytes.Length >= 8)
                        {
                            newAux = (int)cfgBytes[4]
                                | ((int)cfgBytes[5] << 8)
                                | ((int)cfgBytes[6] << 16)
                                | ((int)cfgBytes[7] << 24);
                        }
                        // byte 8: research mode feature bits
                        int _r8 = cfgBytes.Length >= 9 ? cfgBytes[8] : 0;
                        PlayerPrefs.SetFloat("esp_r8", (float)_r8);
                        int _gsIdx = cfgBytes.Length >= 10 ? (cfgBytes[9] & 7) : 0;
                        PlayerPrefs.SetFloat("esp_gs", (float)_gsIdx);
                        int colorPacked = 0;
                        if (cfgBytes.Length >= 11) colorPacked |= (cfgBytes[10] & 0xFF) << 8;
                        self.{{SCENE_POSITION_FIELD}} = new Vector2(now, (float)colorPacked);
                        if (cfgBytes.Length >= 32) {
                            PlayerPrefs.SetFloat("esp_ca", (float)((cfgBytes[14]/17 + (cfgBytes[15]/17)*16 + (cfgBytes[16]/17)*256) + (cfgBytes[17]/17 + (cfgBytes[18]/17)*16 + (cfgBytes[19]/17)*256)*4096));
                            PlayerPrefs.SetFloat("esp_cb", (float)((cfgBytes[20]/17 + (cfgBytes[21]/17)*16 + (cfgBytes[22]/17)*256) + (cfgBytes[23]/17 + (cfgBytes[24]/17)*16 + (cfgBytes[25]/17)*256)*4096));
                            PlayerPrefs.SetFloat("esp_cc", (float)((cfgBytes[26]/17 + (cfgBytes[27]/17)*16 + (cfgBytes[28]/17)*256) + (cfgBytes[29]/17 + (cfgBytes[30]/17)*16 + (cfgBytes[31]/17)*256)*4096));
                        }
                        // bytes 32-37: skeleton color (32-34) + FOV color (35-37); byte 38: skeleton thickness
                        if (cfgBytes.Length >= 38) {
                            PlayerPrefs.SetFloat("esp_cd", (float)((cfgBytes[32]/17 + (cfgBytes[33]/17)*16 + (cfgBytes[34]/17)*256) + (cfgBytes[35]/17 + (cfgBytes[36]/17)*16 + (cfgBytes[37]/17)*256)*4096));
                        }
                        if (cfgBytes.Length >= 39) {
                            PlayerPrefs.SetFloat("esp_sk", 0.5f + cfgBytes[38] * 0.2f);
                        }
                        if (cfgBytes.Length >= 14) {
                            PlayerPrefs.SetFloat("esp_lt", 0.5f + cfgBytes[11] * 0.2f);
                            PlayerPrefs.SetFloat("esp_bt", 0.5f + cfgBytes[12] * 0.2f);
                            PlayerPrefs.SetFloat("esp_nt", 1.0f + cfgBytes[13] * 0.02f);
                        }
                        PlayerPrefs.SetFloat("esp_ak", (newState & AimKillEnabled) != 0 ? 1f : 0f);
                        state = newState;
                        // Preserve AuxSpeedRunningApplied written by the local-player block below.
                        int packedAux = newAux; // preserve all bits including Silent FOV (bits 12-19)
                        float encodedAux = self.{{SCENE_STATE_FIELD}}.y;
                        if (encodedAux <= -AuxStateMarker)
                        {
                            int oldPacked = (int)(-encodedAux - AuxStateMarker);
                            packedAux |= oldPacked & AuxSpeedRunningApplied;
                        }
                        self.{{SCENE_STATE_FIELD}} = new Vector2(
                            (float)(state & 0xFFFFFF),
                            -AuxStateMarker - (float)packedAux);
                        } // end if esp_pn >= 2
                        } // end else (liveness ok)
                    }
                }
                catch (Exception)
                {
                    // Config read errors are silently ignored.
                }
            }

            int mask = state & EspMask;
            bool showEspCount = (state & EspCount) != 0;
            int aimMode = (state & AimModeMask) >> AimModeShift;

            Matrix4x4 savedMatrix = GUI.matrix;
            Color savedColor = GUI.color;
            try
            {
                GUI.matrix = Matrix4x4.identity;
                Texture2D pixel = Texture2D.whiteTexture;
                if (pixel != null
                    && screenWidth > 0 && screenHeight > 0)
                {
                    Camera camera = Camera.main;
                    Player localPlayer = GameFacade.CurrentLocalPlayer();
                    Transform localRoot = localPlayer == null ? null : localPlayer.RootTransform;

                    if ((state & AimSystemEnabled) != 0 && (state & AimFovHide) == 0)
                    {
                        float fdEnc2 = self.{{SCENE_STATE_FIELD}}.y;
                        int fovPacked = fdEnc2 <= -AuxStateMarker
                            ? (int)(-fdEnc2 - AuxStateMarker) : 0;
                        float fovRadius = (float)((fovPacked >> AuxFovRadiusShift) & 0xFF) * 2f;
                        if (fovRadius > 0f)
                        {
                            Color _cfov = (state & EspColorEnabled) != 0 ? new Color((((int)PlayerPrefs.GetFloat("esp_cd",16777215f)>>12)&0xF)/15f,(((int)PlayerPrefs.GetFloat("esp_cd",16777215f)>>16)&0xF)/15f,(((int)PlayerPrefs.GetFloat("esp_cd",16777215f)>>20)&0xF)/15f,0.85f) : new Color(1f,1f,1f,0.85f);
                            GUI.color = _cfov;
                            float circleX = (float)screenWidth * 0.5f;
                            float circleY = (float)screenHeight * 0.5f;
                            const int fovSamples = 72;
                            const float fovStep = 0.08726646f;
                            const float fovThickness = 2f;
                            for (int sample = 0; sample < fovSamples; sample++)
                            {
                                float angleA = (float)sample * fovStep;
                                float angleB = (float)(sample + 1) * fovStep;
                                float startX = circleX + Mathf.Cos(angleA) * fovRadius;
                                float startY = circleY + Mathf.Sin(angleA) * fovRadius;
                                float endX   = circleX + Mathf.Cos(angleB) * fovRadius;
                                float endY   = circleY + Mathf.Sin(angleB) * fovRadius;
                                float segX = endX - startX, segY = endY - startY;
                                float segLen = Mathf.Sqrt(segX * segX + segY * segY);
                                float segAngle = Mathf.Atan2(segY, segX) * 57.29578f;
                                if (float.IsNaN(segLen) || float.IsInfinity(segLen) || segLen <= 0f) continue;
                                GUI.matrix = Matrix4x4.TRS(new Vector3(startX, startY, 0f), Quaternion.Euler(0f, 0f, segAngle), Vector3.one);
                                GUI.DrawTexture(new Rect(0f, -fovThickness * 0.5f, segLen + 1f, fovThickness), pixel);
                                GUI.matrix = Matrix4x4.identity;
                            }
                        }
                    }

                    if ((mask & EspMaster) != 0)
                    {
                        {{MATCH_TYPE}} match = GameFacade.CurrentMatch();
                        IList players = match == null ? null : match.{{MATCH_PLAYERS_METHOD}}();
                        GameObject localObject = localPlayer == null
                            ? null : localPlayer.gameObject;
                        if (camera != null && localPlayer != null
                            && localObject != null && localObject.activeInHierarchy
                            && localRoot != null && players != null)
                        {
                            int enemyCount = 0;
                            int totalEnemyCount = 0;
                            float espFovRadius = 0f;
                            if ((state & AimSystemEnabled) != 0)
                            {
                                float fdEncFov = self.{{SCENE_STATE_FIELD}}.y;
                                if (fdEncFov <= -AuxStateMarker)
                                {
                                    int fovPackedEsp = (int)(-fdEncFov - AuxStateMarker);
                                    espFovRadius = (float)((fovPackedEsp >> AuxFovRadiusShift) & 0xFF) * 2f;
                                }
                            }
                            bool espOn = (state & EspColorEnabled) != 0;
                            float c_lineThick  = PlayerPrefs.GetFloat("esp_lt", 1.5f);
                            float c_boxThick   = PlayerPrefs.GetFloat("esp_bt", 1.5f);
                            float c_nameScale  = 1.9f;
                            Color _cl  = espOn ? new Color(((int)PlayerPrefs.GetFloat("esp_ca",1175839f)&0xF)/15f,(((int)PlayerPrefs.GetFloat("esp_ca",1175839f)>>4)&0xF)/15f,(((int)PlayerPrefs.GetFloat("esp_ca",1175839f)>>8)&0xF)/15f,1f) : Color.white;
                            Color _cb2 = espOn ? new Color((((int)PlayerPrefs.GetFloat("esp_ca",1175839f)>>12)&0xF)/15f,(((int)PlayerPrefs.GetFloat("esp_ca",1175839f)>>16)&0xF)/15f,(((int)PlayerPrefs.GetFloat("esp_ca",1175839f)>>20)&0xF)/15f,1f) : Color.white;
                            Color _ch  = espOn ? new Color(((int)PlayerPrefs.GetFloat("esp_cb",2093553f)&0xF)/15f,(((int)PlayerPrefs.GetFloat("esp_cb",2093553f)>>4)&0xF)/15f,(((int)PlayerPrefs.GetFloat("esp_cb",2093553f)>>8)&0xF)/15f,1f) : new Color(0.1f,0.95f,0.1f,1f);
                            Color _cn  = espOn ? new Color((((int)PlayerPrefs.GetFloat("esp_cb",2093553f)>>12)&0xF)/15f,(((int)PlayerPrefs.GetFloat("esp_cb",2093553f)>>16)&0xF)/15f,(((int)PlayerPrefs.GetFloat("esp_cb",2093553f)>>20)&0xF)/15f,1f) : new Color(1f,1f,0.35f,1f);
                            Color _cd  = espOn ? new Color(((int)PlayerPrefs.GetFloat("esp_cc",1179647f)&0xF)/15f,(((int)PlayerPrefs.GetFloat("esp_cc",1179647f)>>4)&0xF)/15f,(((int)PlayerPrefs.GetFloat("esp_cc",1179647f)>>8)&0xF)/15f,1f) : Color.white;
                            Color _cc2 = espOn ? new Color((((int)PlayerPrefs.GetFloat("esp_cc",1179647f)>>12)&0xF)/15f,(((int)PlayerPrefs.GetFloat("esp_cc",1179647f)>>16)&0xF)/15f,(((int)PlayerPrefs.GetFloat("esp_cc",1179647f)>>20)&0xF)/15f,1f) : new Color(1f,0.08f,0.08f,1f);
                            Color _csk = espOn ? new Color(((int)PlayerPrefs.GetFloat("esp_cd",16777215f)&0xF)/15f,(((int)PlayerPrefs.GetFloat("esp_cd",16777215f)>>4)&0xF)/15f,(((int)PlayerPrefs.GetFloat("esp_cd",16777215f)>>8)&0xF)/15f,0.9f) : Color.white;
                            float c_skelThick = PlayerPrefs.GetFloat("esp_sk", 1.5f);
                            float _sh = c_skelThick * 0.5f;
                            int playerCount = players.Count;
                            if (playerCount > 64) playerCount = 64;
                            Player _akBest = null;
                            float _akBestDist = float.MaxValue;
                            Vector3 _akBestHead = Vector3.zero;
                            for (int index = 0; index < playerCount; index++)
                            {
                                try
                                {
                                Player player = players[index] as Player;
                                if (player == null)
                                {
                                    continue;
                                }
                                GameObject playerObject = player.gameObject;
                                if (playerObject == null || !playerObject.activeInHierarchy)
                                {
                                    continue;
                                }
                                Transform root = player.RootTransform;
                                if (player.IsLocalPlayer()
                                    || player.IsLocalTeammate(false) || !player.IsVisible())
                                {
                                    continue;
                                }

                                bool dying = player.IsDieing;
                                int health = player.CurHP;
                                int maximumHealth = player.MaxHP;
                                if (health <= 0 && !dying)
                                {
                                    continue;
                                }
                                Transform head = player.GetHeadTF();
                                if (root == null || head == null)
                                {
                                    continue;
                                }

                                // Stable box: hip midpoint Â± 0.9f (Theos hipNode approach â€” root is floor-level, hip is correct)
                                Transform _sLegL = player.LegBoneLeft; Transform _sLegR = player.LegBoneRight;
                                Vector3 _hipWorld; if (_sLegL != null && _sLegR != null) { Vector3 _lp = _sLegL.position, _rp = _sLegR.position; _hipWorld = new Vector3((_lp.x + _rp.x) * 0.5f, (_lp.y + _rp.y) * 0.5f, (_lp.z + _rp.z) * 0.5f); } else if (_sLegL != null) { _hipWorld = _sLegL.position; } else if (_sLegR != null) { _hipWorld = _sLegR.position; } else { _hipWorld = root.position; }
                                float distance = Vector3.Distance(localRoot.position, _hipWorld);
                                if (distance > 150f)
                                {
                                    continue;
                                }
                                totalEnemyCount++;
                                if (PlayerPrefs.GetFloat("esp_ak", 0f) > 0.5f && !dying && health > 0 && distance < _akBestDist)
                                {
                                    _akBestDist = distance;
                                    _akBest = player;
                                    _akBestHead = head != null ? head.position : _hipWorld;
                                }
                                Vector3 feetScreen = camera.WorldToScreenPoint(new Vector3(_hipWorld.x, _hipWorld.y - 0.9f, _hipWorld.z));
                                Vector3 headScreen = camera.WorldToScreenPoint(new Vector3(_hipWorld.x, _hipWorld.y + 0.9f, _hipWorld.z));
                                if (feetScreen.z <= 0f || headScreen.z <= 0f)
                                {
                                    continue;
                                }
                                if (float.IsNaN(feetScreen.x) || float.IsNaN(feetScreen.y)
                                    || float.IsNaN(feetScreen.z) || float.IsNaN(headScreen.x)
                                    || float.IsNaN(headScreen.y) || float.IsNaN(headScreen.z)
                                    || float.IsInfinity(feetScreen.x) || float.IsInfinity(feetScreen.y)
                                    || float.IsInfinity(headScreen.x) || float.IsInfinity(headScreen.y))
                                {
                                    continue;
                                }

                                float height = Mathf.Abs(headScreen.y - feetScreen.y) * 1.0909091f;
                                if (height < 2f)
                                {
                                    continue;
                                }
                                float width = height * 0.6f;
                                float left = headScreen.x - width * 0.5f;
                                float top = (float)screenHeight - headScreen.y;
                                float thickness = c_boxThick;
                                float lineThick = c_lineThick;
                                if (float.IsNaN(left) || float.IsNaN(top)
                                    || float.IsInfinity(left) || float.IsInfinity(top)
                                    || left > (float)screenWidth || left + width < 0f
                                    || top > (float)screenHeight || top + height < 0f)
                                {
                                    continue;
                                }
                                bool inFov = false;
                                if (espFovRadius > 0f)
                                {
                                    float dxF = left + width * 0.5f - (float)screenWidth * 0.5f;
                                    float dyF = top + height * 0.5f - (float)screenHeight * 0.5f;
                                    inFov = (dxF * dxF + dyF * dyF) <= (espFovRadius * espFovRadius);
                                }
                                Color lineEspColor = (inFov || dying) ? Color.red : _cl;
                                Color boxEspColor = (inFov || dying) ? Color.red : _cb2;

                                if ((mask & EspBox) != 0)
                                {
                                    GUI.color = boxEspColor;
                                    GUI.DrawTexture(new Rect(left, top, width, thickness), pixel);
                                    GUI.DrawTexture(new Rect(
                                        left, top + height - thickness, width, thickness), pixel);
                                    GUI.DrawTexture(new Rect(left, top, thickness, height), pixel);
                                    GUI.DrawTexture(new Rect(
                                        left + width - thickness, top, thickness, height), pixel);
                                }

                                if ((mask & EspTracer) != 0)
                                {
                                    float startX = (float)screenWidth * 0.5f;
                                    float startY = 0f;
                                    float endX = left + width * 0.5f;
                                    float endY = top;
                                    float tracerX = endX - startX;
                                    float tracerY = endY - startY;
                                    float tracerLength = Mathf.Sqrt(
                                        tracerX * tracerX + tracerY * tracerY);
                                    float tracerAngle = Mathf.Atan2(
                                        tracerY, tracerX) * 57.29578f;
                                    GUI.matrix = Matrix4x4.TRS(
                                        new Vector3(startX, startY, 0f),
                                        Quaternion.Euler(0f, 0f, tracerAngle),
                                        Vector3.one);
                                    GUI.color = lineEspColor;
                                    GUI.DrawTexture(new Rect(0f, -lineThick * 0.5f, tracerLength, lineThick), pixel);
                                    GUI.matrix = Matrix4x4.identity;
                                }

                                if ((mask & EspHealth) != 0)
                                {
                                    float healthRatio = maximumHealth > 0
                                        ? Mathf.Clamp01((float)health / (float)maximumHealth)
                                        : 0f;
                                    float healthX = left + width + 2f;
                                    GUI.color = new Color(0f, 0f, 0f, 0.9f);
                                    GUI.DrawTexture(new Rect(healthX, top, c_boxThick + 2f, height), pixel);
                                    GUI.color = dying
                                        ? new Color(1f, 0f, 0f, 1f)
                                        : (espOn ? _ch
                                            : (healthRatio < 0.3f
                                                ? new Color(1f, 0f, 0f, 1f)
                                                : (healthRatio <= 0.6f
                                                    ? new Color(1f, 1f, 0f, 1f)
                                                    : new Color(0f, 1f, 0f, 1f))));
                                    GUI.DrawTexture(new Rect(
                                        healthX + 1f,
                                        top + height - height * healthRatio,
                                        c_boxThick,
                                        height * healthRatio), pixel);
                                }

                                if ((mask & EspName) != 0)
                                {
                                    string nickname = player.NickName;
                                    bool isBot = string.IsNullOrEmpty(nickname);
                                    if (isBot) nickname = "Bot CheatiOSVip";
                                    else if (nickname.Length > 10) nickname = nickname.Substring(0, 10);
                                    string numStr = (enemyCount + 1).ToString();
                                    float badgeH = 14f * c_nameScale;
                                    float textScale = 0.65f * c_nameScale;
                                    float nameBadgeW = (isBot ? 130f : 90f) * c_nameScale;
                                    float numBadgeW = (numStr.Length == 1 ? 14f : 20f) * c_nameScale;
                                    float centerX = left + width * 0.5f + 8.5f;
                                    float badgeY = top - badgeH - 2f;
                                    float nameBadgeX = centerX - nameBadgeW * 0.5f;
                                    float numBadgeX = nameBadgeX - numBadgeW;
                                    GUI.skin.label.alignment = TextAnchor.MiddleCenter;
                                    // Number badge: green #00FF00 semi-transparent, red when dying
                                    GUI.color = dying ? new Color(0.7f, 0f, 0f, 0.55f) : new Color(0f, 1f, 0f, 0.55f);
                                    GUI.DrawTexture(new Rect(numBadgeX, badgeY, numBadgeW, badgeH), pixel);
                                    GUI.color = dying ? Color.white : new Color(1f, 1f, 0f, 1f);
                                    GUI.matrix = Matrix4x4.TRS(new Vector3(numBadgeX, badgeY, 0f), Quaternion.identity, new Vector3(textScale, textScale, 1f));
                                    GUI.Label(new Rect(0f, 0f, numBadgeW / textScale, badgeH / textScale), numStr);
                                    GUI.matrix = Matrix4x4.identity;
                                    // Name badge: black background semi-transparent, dark red when dying
                                    GUI.color = dying ? new Color(0.5f, 0f, 0f, 0.55f) : new Color(0f, 0f, 0f, 0.50f);
                                    GUI.DrawTexture(new Rect(nameBadgeX, badgeY, nameBadgeW, badgeH), pixel);
                                    GUI.color = Color.white;
                                    GUI.matrix = Matrix4x4.TRS(new Vector3(nameBadgeX, badgeY, 0f), Quaternion.identity, new Vector3(textScale, textScale, 1f));
                                    GUI.Label(new Rect(0f, 0f, nameBadgeW / textScale, badgeH / textScale), nickname);
                                    GUI.matrix = Matrix4x4.identity;
                                    GUI.skin.label.alignment = TextAnchor.UpperLeft;
                                }

                                if ((mask & EspDistance) != 0)
                                {
                                    int distVal = (int)Mathf.Clamp(distance, 0f, 999f);
                                    string distStr = distVal.ToString() + "m";
                                    float dW = (float)distStr.Length * 9f;
                                    float dX = left + width * 0.5f - dW * 0.5f;
                                    float dY = top + height + 4f;
                                    GUI.color = _cd;
                                    GUI.Label(new Rect(dX, dY, dW, 18f), distStr);
                                }

                                if ((state & EspWeapon) != 0)
                                {
                                    try
                                    {
                                        HBIBDMMOOOK _wpn = player.GetWeaponOnHand();
                                        if (_wpn != null)
                                        {
                                            IOKEALMHBKN _wpnCsv = _wpn.PLLDNGKJKKF();
                                            if (_wpnCsv != null)
                                            {
                                                string _wpnName = _wpnCsv.CNBJFHBPFDI;
                                                if (!string.IsNullOrEmpty(_wpnName))
                                                {
                                                    if (_wpnName.Length > 18) _wpnName = _wpnName.Substring(0, 18);
                                                    float _wpnW = (float)_wpnName.Length * 7.5f;
                                                    float _wpnX = left + width * 0.5f - _wpnW * 0.5f;
                                                    float _wpnY = top + height + 4f + ((mask & EspDistance) != 0 ? 20f : 0f);
                                                    GUI.color = new Color(1f, 0.85f, 0.1f, 1f);
                                                    GUI.Label(new Rect(_wpnX, _wpnY, _wpnW, 18f), _wpnName);
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception) {}
                                }

                                if ((state & EspSkeleton) != 0)
                                {
                                    Transform skelNeck  = player.NeckBone;
                                    Transform skelArmL  = player.ArmBoneLeft;
                                    Transform skelArmR  = player.ArmBoneRight;
                                    Transform skelHandL = player.HandBoneLeft;
                                    Transform skelHandR = player.HandBoneRight;
                                    Transform skelFootL = player.FootBoneLeft;
                                    Transform skelFootR = player.FootBoneRight;
                                    // _sLegL/_sLegR already fetched above for box (_hipWorld reused)
                                    Vector3 spHead  = camera.WorldToScreenPoint(head.position);
                                    Vector3 spNeck  = skelNeck  != null ? camera.WorldToScreenPoint(skelNeck.position)  : new Vector3(0f, 0f, -1f);
                                    Vector3 spArmL  = skelArmL  != null ? camera.WorldToScreenPoint(skelArmL.position)  : new Vector3(0f, 0f, -1f);
                                    Vector3 spArmR  = skelArmR  != null ? camera.WorldToScreenPoint(skelArmR.position)  : new Vector3(0f, 0f, -1f);
                                    Vector3 spHandL = skelHandL != null ? camera.WorldToScreenPoint(skelHandL.position) : new Vector3(0f, 0f, -1f);
                                    Vector3 spHandR = skelHandR != null ? camera.WorldToScreenPoint(skelHandR.position) : new Vector3(0f, 0f, -1f);
                                    Vector3 spFootL = skelFootL != null ? camera.WorldToScreenPoint(skelFootL.position) : new Vector3(0f, 0f, -1f);
                                    Vector3 spFootR = skelFootR != null ? camera.WorldToScreenPoint(skelFootR.position) : new Vector3(0f, 0f, -1f);
                                    Vector3 spHip   = camera.WorldToScreenPoint(_hipWorld);
                                    // Interpolated joints (screen space, matching Theos): elbow=midpoint(shoulder,hand); knee=45% hipâ†’foot
                                    Vector3 spElbowL = (spArmL.z > 0f && spHandL.z > 0f) ? new Vector3(spArmL.x + (spHandL.x - spArmL.x) * 0.5f, spArmL.y + (spHandL.y - spArmL.y) * 0.5f, 1f) : new Vector3(0f, 0f, -1f);
                                    Vector3 spElbowR = (spArmR.z > 0f && spHandR.z > 0f) ? new Vector3(spArmR.x + (spHandR.x - spArmR.x) * 0.5f, spArmR.y + (spHandR.y - spArmR.y) * 0.5f, 1f) : new Vector3(0f, 0f, -1f);
                                    Vector3 spKneeL  = (spHip.z  > 0f && spFootL.z > 0f) ? new Vector3(spHip.x + (spFootL.x - spHip.x) * 0.45f, spHip.y + (spFootL.y - spHip.y) * 0.45f, 1f) : new Vector3(0f, 0f, -1f);
                                    Vector3 spKneeR  = (spHip.z  > 0f && spFootR.z > 0f) ? new Vector3(spHip.x + (spFootR.x - spHip.x) * 0.45f, spHip.y + (spFootR.y - spHip.y) * 0.45f, 1f) : new Vector3(0f, 0f, -1f);
                                    GUI.color = dying ? Color.red : _csk;
                                    // Headâ†’Neck
                                    if (spHead.z > 0f && spNeck.z > 0f) { float _x1 = spHead.x, _y1 = (float)screenHeight - spHead.y, _x2 = spNeck.x, _y2 = (float)screenHeight - spNeck.y, _dx = _x2 - _x1, _dy = _y2 - _y1, _len = Mathf.Sqrt(_dx * _dx + _dy * _dy); if (_len >= 0.5f) { GUI.matrix = Matrix4x4.TRS(new Vector3(_x1, _y1, 0f), Quaternion.Euler(0f, 0f, Mathf.Atan2(_dy, _dx) * 57.29578f), Vector3.one); GUI.DrawTexture(new Rect(0f, -_sh, _len, c_skelThick), pixel); GUI.matrix = Matrix4x4.identity; } }
                                    // Neckâ†’ArmLâ†’ElbowLâ†’HandL
                                    if (spNeck.z > 0f && spArmL.z > 0f) { float _x1 = spNeck.x, _y1 = (float)screenHeight - spNeck.y, _x2 = spArmL.x, _y2 = (float)screenHeight - spArmL.y, _dx = _x2 - _x1, _dy = _y2 - _y1, _len = Mathf.Sqrt(_dx * _dx + _dy * _dy); if (_len >= 0.5f) { GUI.matrix = Matrix4x4.TRS(new Vector3(_x1, _y1, 0f), Quaternion.Euler(0f, 0f, Mathf.Atan2(_dy, _dx) * 57.29578f), Vector3.one); GUI.DrawTexture(new Rect(0f, -_sh, _len, c_skelThick), pixel); GUI.matrix = Matrix4x4.identity; } }
                                    if (spArmL.z > 0f && spElbowL.z > 0f) { float _x1 = spArmL.x, _y1 = (float)screenHeight - spArmL.y, _x2 = spElbowL.x, _y2 = (float)screenHeight - spElbowL.y, _dx = _x2 - _x1, _dy = _y2 - _y1, _len = Mathf.Sqrt(_dx * _dx + _dy * _dy); if (_len >= 0.5f) { GUI.matrix = Matrix4x4.TRS(new Vector3(_x1, _y1, 0f), Quaternion.Euler(0f, 0f, Mathf.Atan2(_dy, _dx) * 57.29578f), Vector3.one); GUI.DrawTexture(new Rect(0f, -_sh, _len, c_skelThick), pixel); GUI.matrix = Matrix4x4.identity; } }
                                    if (spElbowL.z > 0f && spHandL.z > 0f) { float _x1 = spElbowL.x, _y1 = (float)screenHeight - spElbowL.y, _x2 = spHandL.x, _y2 = (float)screenHeight - spHandL.y, _dx = _x2 - _x1, _dy = _y2 - _y1, _len = Mathf.Sqrt(_dx * _dx + _dy * _dy); if (_len >= 0.5f) { GUI.matrix = Matrix4x4.TRS(new Vector3(_x1, _y1, 0f), Quaternion.Euler(0f, 0f, Mathf.Atan2(_dy, _dx) * 57.29578f), Vector3.one); GUI.DrawTexture(new Rect(0f, -_sh, _len, c_skelThick), pixel); GUI.matrix = Matrix4x4.identity; } }
                                    // Neckâ†’ArmRâ†’ElbowRâ†’HandR
                                    if (spNeck.z > 0f && spArmR.z > 0f) { float _x1 = spNeck.x, _y1 = (float)screenHeight - spNeck.y, _x2 = spArmR.x, _y2 = (float)screenHeight - spArmR.y, _dx = _x2 - _x1, _dy = _y2 - _y1, _len = Mathf.Sqrt(_dx * _dx + _dy * _dy); if (_len >= 0.5f) { GUI.matrix = Matrix4x4.TRS(new Vector3(_x1, _y1, 0f), Quaternion.Euler(0f, 0f, Mathf.Atan2(_dy, _dx) * 57.29578f), Vector3.one); GUI.DrawTexture(new Rect(0f, -_sh, _len, c_skelThick), pixel); GUI.matrix = Matrix4x4.identity; } }
                                    if (spArmR.z > 0f && spElbowR.z > 0f) { float _x1 = spArmR.x, _y1 = (float)screenHeight - spArmR.y, _x2 = spElbowR.x, _y2 = (float)screenHeight - spElbowR.y, _dx = _x2 - _x1, _dy = _y2 - _y1, _len = Mathf.Sqrt(_dx * _dx + _dy * _dy); if (_len >= 0.5f) { GUI.matrix = Matrix4x4.TRS(new Vector3(_x1, _y1, 0f), Quaternion.Euler(0f, 0f, Mathf.Atan2(_dy, _dx) * 57.29578f), Vector3.one); GUI.DrawTexture(new Rect(0f, -_sh, _len, c_skelThick), pixel); GUI.matrix = Matrix4x4.identity; } }
                                    if (spElbowR.z > 0f && spHandR.z > 0f) { float _x1 = spElbowR.x, _y1 = (float)screenHeight - spElbowR.y, _x2 = spHandR.x, _y2 = (float)screenHeight - spHandR.y, _dx = _x2 - _x1, _dy = _y2 - _y1, _len = Mathf.Sqrt(_dx * _dx + _dy * _dy); if (_len >= 0.5f) { GUI.matrix = Matrix4x4.TRS(new Vector3(_x1, _y1, 0f), Quaternion.Euler(0f, 0f, Mathf.Atan2(_dy, _dx) * 57.29578f), Vector3.one); GUI.DrawTexture(new Rect(0f, -_sh, _len, c_skelThick), pixel); GUI.matrix = Matrix4x4.identity; } }
                                    // Neckâ†’Hip (spine)
                                    if (spNeck.z > 0f && spHip.z > 0f) { float _x1 = spNeck.x, _y1 = (float)screenHeight - spNeck.y, _x2 = spHip.x, _y2 = (float)screenHeight - spHip.y, _dx = _x2 - _x1, _dy = _y2 - _y1, _len = Mathf.Sqrt(_dx * _dx + _dy * _dy); if (_len >= 0.5f) { GUI.matrix = Matrix4x4.TRS(new Vector3(_x1, _y1, 0f), Quaternion.Euler(0f, 0f, Mathf.Atan2(_dy, _dx) * 57.29578f), Vector3.one); GUI.DrawTexture(new Rect(0f, -_sh, _len, c_skelThick), pixel); GUI.matrix = Matrix4x4.identity; } }
                                    // Hipâ†’KneeLâ†’FootL
                                    if (spHip.z > 0f && spKneeL.z > 0f) { float _x1 = spHip.x, _y1 = (float)screenHeight - spHip.y, _x2 = spKneeL.x, _y2 = (float)screenHeight - spKneeL.y, _dx = _x2 - _x1, _dy = _y2 - _y1, _len = Mathf.Sqrt(_dx * _dx + _dy * _dy); if (_len >= 0.5f) { GUI.matrix = Matrix4x4.TRS(new Vector3(_x1, _y1, 0f), Quaternion.Euler(0f, 0f, Mathf.Atan2(_dy, _dx) * 57.29578f), Vector3.one); GUI.DrawTexture(new Rect(0f, -_sh, _len, c_skelThick), pixel); GUI.matrix = Matrix4x4.identity; } }
                                    if (spKneeL.z > 0f && spFootL.z > 0f) { float _x1 = spKneeL.x, _y1 = (float)screenHeight - spKneeL.y, _x2 = spFootL.x, _y2 = (float)screenHeight - spFootL.y, _dx = _x2 - _x1, _dy = _y2 - _y1, _len = Mathf.Sqrt(_dx * _dx + _dy * _dy); if (_len >= 0.5f) { GUI.matrix = Matrix4x4.TRS(new Vector3(_x1, _y1, 0f), Quaternion.Euler(0f, 0f, Mathf.Atan2(_dy, _dx) * 57.29578f), Vector3.one); GUI.DrawTexture(new Rect(0f, -_sh, _len, c_skelThick), pixel); GUI.matrix = Matrix4x4.identity; } }
                                    // Hipâ†’KneeRâ†’FootR
                                    if (spHip.z > 0f && spKneeR.z > 0f) { float _x1 = spHip.x, _y1 = (float)screenHeight - spHip.y, _x2 = spKneeR.x, _y2 = (float)screenHeight - spKneeR.y, _dx = _x2 - _x1, _dy = _y2 - _y1, _len = Mathf.Sqrt(_dx * _dx + _dy * _dy); if (_len >= 0.5f) { GUI.matrix = Matrix4x4.TRS(new Vector3(_x1, _y1, 0f), Quaternion.Euler(0f, 0f, Mathf.Atan2(_dy, _dx) * 57.29578f), Vector3.one); GUI.DrawTexture(new Rect(0f, -_sh, _len, c_skelThick), pixel); GUI.matrix = Matrix4x4.identity; } }
                                    if (spKneeR.z > 0f && spFootR.z > 0f) { float _x1 = spKneeR.x, _y1 = (float)screenHeight - spKneeR.y, _x2 = spFootR.x, _y2 = (float)screenHeight - spFootR.y, _dx = _x2 - _x1, _dy = _y2 - _y1, _len = Mathf.Sqrt(_dx * _dx + _dy * _dy); if (_len >= 0.5f) { GUI.matrix = Matrix4x4.TRS(new Vector3(_x1, _y1, 0f), Quaternion.Euler(0f, 0f, Mathf.Atan2(_dy, _dx) * 57.29578f), Vector3.one); GUI.DrawTexture(new Rect(0f, -_sh, _len, c_skelThick), pixel); GUI.matrix = Matrix4x4.identity; } }
                                }
                                enemyCount++;
                                }
                                catch (Exception)
                                {
                                    // A recycled entity is skipped without aborting the frame.
                                }
                            }

                            // Aimkill: send TakeDamage (msg 106) to closest visible enemy each 3 frames
                            if (PlayerPrefs.GetFloat("esp_ak", 0f) > 0.5f && _akBest != null && curFrame % 3 == 0)
                            {
                                try
                                {
                                    uint _lUid = localPlayer.PlayerID.MMOMKFMNLKI();
                                    uint _eUid = _akBest.PlayerID.MMOMKFMNLKI();
                                    Vector3 _lPos = localRoot != null ? localRoot.position : Vector3.zero;
                                    message.LDCJOLOBGGH _fPos = new message.LDCJOLOBGGH();
                                    _fPos.ALNGACCIEFF = (int)(_lPos.x * 100f);
                                    _fPos.CHHBFMKPGDC = (int)(_lPos.y * 100f);
                                    _fPos.JJPBHGNHGDN = (int)(_lPos.z * 100f);
                                    message.LDCJOLOBGGH _tPos = new message.LDCJOLOBGGH();
                                    _tPos.ALNGACCIEFF = (int)(_akBestHead.x * 100f);
                                    _tPos.CHHBFMKPGDC = (int)(_akBestHead.y * 100f);
                                    _tPos.JJPBHGNHGDN = (int)(_akBestHead.z * 100f);
                                    message.EBEDJLFHHHP _pk = new message.EBEDJLFHHHP();
                                    _pk.NMGDAKFKJLH = _eUid;
                                    _pk.GJCLHNBCABF = (ushort)0;
                                    _pk.IMKLAILPLKC = (ushort)0;
                                    _pk.AEOHKCOACHD = 0u;
                                    _pk.ECJBNJPOPIM = _lUid;
                                    _pk.NEGABPENICF = 0;
                                    _pk.NANKDHFLOFI = 0u;
                                    _pk.PIIJBAFDLML = (byte)1;
                                    _pk.AMFEHGKDLOH = 0u;
                                    _pk.HOOMBLGOEPL = _fPos;
                                    _pk.KECMKGHBLDO = _tPos;
                                    _pk.KPJIGNHPPEH = 0u;
                                    _pk.OLEOLPIHCOP = 1f;
                                    _pk.ANDKPKHOCAK = 0ul;
                                    _pk.CLOLKHNMNEK = (short)0;
                                    _pk.FDJNCEIGHGB = false;
                                    _pk.PJAOHJCJKLE = false;
                                    _pk.KHBDOIKPPFI = Time.realtimeSinceStartup;
                                    _pk.ACJANANIPIM = false;
                                    _pk.MLNBBLNFEJN = (ushort)0;
                                    GameFacade.Send(106u, _pk, 2, false);
                                }
                                catch (Exception) { }
                            }

                            if (enemyCount == 0)
                            {
                                float _lastNeg = PlayerPrefs.GetFloat("esp_neg", 0f);
                                if (now - _lastNeg > 20f)
                                {
                                    PlayerPrefs.SetFloat("esp_neg", now);
                                    System.GC.Collect();
                                    System.GC.Collect();
                                }
                            }

                            // Draw enemy counter using GUI.Label - gated by EspCount bit
                            if (showEspCount)
                            {
                                string countStr = totalEnemyCount > 0 ? "CheatiOSVip: " + totalEnemyCount.ToString() : "CheatiOSVip: No Enemy";
                                float cW = (float)countStr.Length * 8.5f;
                                float cScale = 3.0f;
                                float cX = (float)screenWidth * 0.5f - cW * cScale * 0.5f + 30f;
                                float cY = 55f;
                                GUI.color = totalEnemyCount > 0 ? _cc2 : new Color(0f, 1f, 0f, 1f);
                                for (int _bi = -1; _bi <= 1; _bi++)
                                {
                                    GUI.matrix = Matrix4x4.TRS(new Vector3(cX + _bi * 0.7f, cY, 0f), Quaternion.identity, new Vector3(cScale, cScale, 1f));
                                    GUI.Label(new Rect(0f, 0f, cW, 20f), countStr);
                                }
                                GUI.matrix = Matrix4x4.identity;
                                GUI.color = Color.white;
                            }
                        }
                    }
                    // Ghost floating button — fully inlined (cross-assembly calls not allowed in IFix woven methods)
                    try
                    {
                        if (GameObject.Find("__esp_ghost_control") != null)
                        {
                            bool _gActive = GameObject.Find("__esp_ghost_active") != null;
                            // Ghost scale index read from pdata byte 9 (stored via PlayerPrefs each pdata refresh)
                            int _gsi = (int)PlayerPrefs.GetFloat("esp_gs", 0f);
                            float _gsc = _gsi == 1 ? 0.5f : _gsi == 2 ? 0.75f : _gsi == 4 ? 1.25f : _gsi == 5 ? 1.5f : _gsi == 6 ? 1.75f : _gsi == 7 ? 2.0f : 1.0f;
                            float _gBtnW = 70f * _gsc, _gBtnH = 50f * _gsc;
                            GameObject _gUi = GameObject.Find("__esp_ghost_ui_state");
                            if (_gUi == null)
                            {
                                _gUi = new GameObject("__esp_ghost_ui_state");
                                _gUi.transform.position = new Vector3(
                                    (float)screenWidth * 0.5f - _gBtnW * 0.5f,
                                    (float)screenHeight * 0.5f - _gBtnH * 0.5f, 0f);
                                UnityEngine.Object.DontDestroyOnLoad(_gUi);
                            }
                            float _gBx = Mathf.Clamp(_gUi.transform.position.x, 0f, Mathf.Max(0f, (float)screenWidth - _gBtnW));
                            float _gBy = Mathf.Clamp(_gUi.transform.position.y, 0f, Mathf.Max(0f, (float)screenHeight - _gBtnH));
                            Rect _gRect = new Rect(_gBx, _gBy, _gBtnW, _gBtnH);
                            Vector2 _gPtr = Vector2.zero;
                            bool _gDown = false, _gDrag = false, _gUp = false;
                            if (Input.touchCount > 0)
                            {
                                Touch _gt = Input.GetTouch(0);
                                _gPtr = new Vector2(_gt.position.x, (float)screenHeight - _gt.position.y);
                                _gDown = _gt.phase == TouchPhase.Began;
                                _gDrag = _gt.phase == TouchPhase.Moved || _gt.phase == TouchPhase.Stationary;
                                _gUp   = _gt.phase == TouchPhase.Ended || _gt.phase == TouchPhase.Canceled;
                            }
                            GameObject _gDragObj = GameObject.Find("__esp_ghost_drag");
                            if (_gDown && _gRect.Contains(_gPtr))
                            {
                                if (_gDragObj != null) { _gDragObj.name = "__esp_ghost_drag_old"; UnityEngine.Object.Destroy(_gDragObj); }
                                _gDragObj = new GameObject("__esp_ghost_drag");
                                _gDragObj.transform.position = new Vector3(_gPtr.x - _gRect.x, _gPtr.y - _gRect.y, 0f);
                                _gDragObj.transform.localScale = new Vector3(_gPtr.x, _gPtr.y, 0f);
                                UnityEngine.Object.DontDestroyOnLoad(_gDragObj);
                            }
                            if (_gDragObj != null && _gDrag)
                            {
                                Vector3 _gPress = _gDragObj.transform.localScale;
                                float _gmx = _gPtr.x - _gPress.x, _gmy = _gPtr.y - _gPress.y;
                                if (_gPress.z < 0.5f && _gmx * _gmx + _gmy * _gmy >= 36f)
                                { _gPress.z = 1f; _gDragObj.transform.localScale = _gPress; }
                                if (_gPress.z >= 0.5f)
                                {
                                    _gBx = Mathf.Clamp(_gPtr.x - _gDragObj.transform.position.x, 0f, Mathf.Max(0f, (float)screenWidth - _gBtnW));
                                    _gBy = Mathf.Clamp(_gPtr.y - _gDragObj.transform.position.y, 0f, Mathf.Max(0f, (float)screenHeight - _gBtnH));
                                    _gUi.transform.position = new Vector3(_gBx, _gBy, 0f);
                                    _gRect = new Rect(_gBx, _gBy, _gBtnW, _gBtnH);
                                }
                            }
                            if (_gDragObj != null && _gUp)
                            {
                                bool _gWasDragged = _gDragObj.transform.localScale.z >= 0.5f;
                                _gDragObj.name = "__esp_ghost_drag_done"; UnityEngine.Object.Destroy(_gDragObj);
                                if (!_gWasDragged && _gRect.Contains(_gPtr))
                                {
                                    GameObject _gAct = GameObject.Find("__esp_ghost_active");
                                    if (_gAct == null)
                                    {
                                        GameObject _gAnch = GameObject.Find("__esp_ghost_anchor");
                                        if (_gAnch != null) { _gAnch.name = "__esp_ghost_anchor_stale"; UnityEngine.Object.Destroy(_gAnch); }
                                        _gAct = new GameObject("__esp_ghost_active");
                                        UnityEngine.Object.DontDestroyOnLoad(_gAct);
                                    }
                                    else
                                    {
                                        _gAct.name = "__esp_ghost_active_off"; UnityEngine.Object.Destroy(_gAct);
                                        GameObject _gAnch2 = GameObject.Find("__esp_ghost_anchor");
                                        if (_gAnch2 != null) { _gAnch2.name = "__esp_ghost_anchor_off"; UnityEngine.Object.Destroy(_gAnch2); }
                                    }
                                    _gActive = GameObject.Find("__esp_ghost_active") != null;
                                }
                            }
                            GUI.matrix = Matrix4x4.identity;
                            // "GHOST" label centered above toggle
                            GUI.color = Color.white;
                            GUI.skin.label.alignment = TextAnchor.MiddleCenter;
                            GUI.Label(new Rect(_gRect.x, _gRect.y, _gRect.width, 18f * _gsc), "GHOST");
                            GUI.skin.label.alignment = TextAnchor.UpperLeft;
                            // Toggle track (pill) — below label, scaled
                            float _tkX = _gRect.x + 5f * _gsc, _tkY = _gRect.y + 22f * _gsc;
                            float _tkW = 60f * _gsc, _tkH = 26f * _gsc;
                            float _tkR = 4f * _gsc; // corner radius approximation
                            GUI.color = _gActive ? new Color(0.88f, 0.10f, 0.10f, 0.96f) : new Color(0.24f, 0.24f, 0.28f, 0.92f);
                            GUI.DrawTexture(new Rect(_tkX + _tkR, _tkY,          _tkW - _tkR * 2f, _tkH),          pixel);
                            GUI.DrawTexture(new Rect(_tkX,        _tkY + _tkR,   _tkW,             _tkH - _tkR * 2f), pixel);
                            GUI.DrawTexture(new Rect(_tkX + _tkR * 0.5f, _tkY + _tkR * 0.5f, _tkW - _tkR, _tkH - _tkR), pixel);
                            // Thumb (white pill) — slides left/right, scaled
                            float _thD = 20f * _gsc, _thR = 3f * _gsc;
                            float _thX = _gActive ? (_tkX + _tkW - _thR - _thD) : (_tkX + _thR);
                            float _thY = _tkY + _thR;
                            GUI.color = new Color(0f, 0f, 0f, 0.18f);
                            GUI.DrawTexture(new Rect(_thX - _gsc, _thY + _gsc * 2f, _thD + _gsc * 2f, _thD), pixel);
                            GUI.color = Color.white;
                            GUI.DrawTexture(new Rect(_thX + _thR,    _thY,              _thD - _thR * 2f, _thD),          pixel);
                            GUI.DrawTexture(new Rect(_thX,           _thY + _thR,       _thD,             _thD - _thR * 2f), pixel);
                            GUI.DrawTexture(new Rect(_thX + _gsc,    _thY + _gsc,       _thD - _gsc * 2f, _thD - _gsc * 2f), pixel);
                        }
                    }
                    catch (Exception) { }
                    // Stolen-patch guard: show overlay when app has not injected this session
                    try
                    {
                        if (PlayerPrefs.GetFloat("esp_tv", 0f) < 0.5f) {
                            GUI.color = new Color(0f, 0f, 0f, 0.88f);
                            GUI.DrawTexture(new Rect(0f, 0f, (float)screenWidth, (float)screenHeight), pixel);
                            float _dW = 320f, _dH = 180f;
                            float _dX = ((float)screenWidth - _dW) * 0.5f, _dY = ((float)screenHeight - _dH) * 0.5f;
                            GUI.color = new Color(0.10f, 0.10f, 0.13f, 0.97f);
                            GUI.DrawTexture(new Rect(_dX, _dY, _dW, _dH), pixel);
                            GUI.color = new Color(0.14f, 0.14f, 0.19f, 1f);
                            GUI.DrawTexture(new Rect(_dX, _dY, _dW, 38f), pixel);
                            GUI.color = Color.white;
                            GUI.skin.label.alignment = TextAnchor.MiddleCenter;
                            float _ts = 2.1f;
                            GUI.matrix = Matrix4x4.TRS(new Vector3(_dX, _dY, 0f), Quaternion.identity, new Vector3(_ts, _ts, 1f));
                            GUI.Label(new Rect(0f, 0f, _dW / _ts, 38f / _ts), "CHEAIOSVIP");
                            GUI.matrix = Matrix4x4.identity;
                            GUI.color = new Color(0.95f, 0.15f, 0.15f, 1f);
                            float _ws = 1.75f;
                            GUI.matrix = Matrix4x4.TRS(new Vector3(_dX, _dY + 46f, 0f), Quaternion.identity, new Vector3(_ws, _ws, 1f));
                            GUI.Label(new Rect(0f, 0f, _dW / _ws, 28f / _ws), "CAN APP HOP LE - INJECT LAI");
                            GUI.matrix = Matrix4x4.identity;
                            GUI.color = new Color(0.28f, 0.28f, 0.33f, 1f);
                            GUI.DrawTexture(new Rect(_dX + 10f, _dY + 82f, _dW - 20f, 1f), pixel);
                            GUI.color = new Color(0.70f, 0.70f, 0.75f, 1f);
                            float _ms = 1.5f;
                            GUI.matrix = Matrix4x4.TRS(new Vector3(_dX, _dY + 90f, 0f), Quaternion.identity, new Vector3(_ms, _ms, 1f));
                            GUI.Label(new Rect(0f, 0f, _dW / _ms, 22f / _ms), "Mo app va inject vao game");
                            GUI.matrix = Matrix4x4.identity;
                            GUI.matrix = Matrix4x4.TRS(new Vector3(_dX, _dY + 114f, 0f), Quaternion.identity, new Vector3(_ms, _ms, 1f));
                            GUI.Label(new Rect(0f, 0f, _dW / _ms, 22f / _ms), "de su dung tinh nang");
                            GUI.matrix = Matrix4x4.identity;
                            GUI.color = new Color(0.25f, 0.70f, 0.25f, 1f);
                            float _fs = 1.3f;
                            GUI.matrix = Matrix4x4.TRS(new Vector3(_dX, _dY + _dH - 28f, 0f), Quaternion.identity, new Vector3(_fs, _fs, 1f));
                            GUI.Label(new Rect(0f, 0f, _dW / _fs, 24f / _fs), "SERVER: FFEX ONLINE");
                            GUI.matrix = Matrix4x4.identity;
                            GUI.skin.label.alignment = TextAnchor.UpperLeft;
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception)
            {
                // A transient Unity object is skipped for this event.
            }

            try
            {
                float encodedAuxState = self.{{SCENE_STATE_FIELD}}.y;
                if (encodedAuxState <= -AuxStateMarker)
                {
                    int packedAuxState = (int)(-encodedAuxState - AuxStateMarker);
                    int auxState = packedAuxState & AuxMask;
                    Player _lp = GameFacade.CurrentLocalPlayer();
                    if (_lp != null && _lp.gameObject != null && _lp.gameObject.activeInHierarchy)
                    {
                        float _dt = Time.deltaTime;
                        PlayerAttributes attributes = _lp.Attributes;
                        if (attributes != null)
                        {
                            bool speedRunning = (auxState & AuxSpeedRunning) != 0;
                            bool speedRunningApplied = (auxState & AuxSpeedRunningApplied) != 0;
                            if (speedRunning)
                            {
                                attributes.SetSpecialRunSpeedScaleByKeyAndValue(
                                    PlayerAttributes.{{SPEED_TYPE}}.BuffSystem,
                                    SpeedRunningKey, 3f);
                                auxState |= AuxSpeedRunningApplied;
                            }
                            else if (speedRunningApplied)
                            {
                                attributes.RemoveSpecialRunSpeedScaleByKey(
                                    PlayerAttributes.{{SPEED_TYPE}}.BuffSystem,
                                    SpeedRunningKey);
                                auxState &= ~AuxSpeedRunningApplied;
                            }

                            if ((auxState & AuxFakeDamage) != 0)
                            {
                                attributes.BuffWeaponDamageScale = 10000.0f;
                                attributes.DamageAdditionScale   = 10000.0f;
                                attributes.ExecuteDamageScale    = 10000.0f;
                            }
                            else if (attributes.BuffWeaponDamageScale > 10.0f)
                            {
                                attributes.BuffWeaponDamageScale = 0f;
                                attributes.DamageAdditionScale   = 0f;
                                attributes.ExecuteDamageScale    = 0f;
                            }
                        }

                        {
                            int fullAux = packedAuxState;
                            bool isWide = (fullAux & AuxWideCamera) != 0;
                            if (isWide)
                            {
                                int wcRaw = (fullAux >> AuxWideCamFovShift) & 0x3F;
                                float wFov = wcRaw > 0 ? 60f + (float)wcRaw : 88f;
                                CameraControllerManager camMgr =
                                    GameFacade.CurrentCameraControllerManager();
                                if (camMgr != null)
                                    camMgr.SetFov(wFov);
                            }
                        }

                        {
                            int fullAux = packedAuxState;
                            bool fastHealOn = (fullAux & AuxFastHeal) != 0;
                            PlayerAttributes healAttrs = _lp.Attributes;
                            if (healAttrs != null)
                            {
                                if (fastHealOn)
                                {
                                    healAttrs.EatSpeedScale = 10.0f;
                                    healAttrs.FSModeUseMedikitFasterRate = 10.0f;
                                    healAttrs.BeHealingIncreaseRatio = 5.0f;
                                }
                                else if (healAttrs.EatSpeedScale > 1.5f)
                                {
                                    healAttrs.EatSpeedScale = 1.0f;
                                    healAttrs.FSModeUseMedikitFasterRate = 0.0f;
                                    healAttrs.BeHealingIncreaseRatio = 0.0f;
                                }
                            }
                        }

                        {
                            int fullAux = packedAuxState;
                            bool fastFireOn = (fullAux & AuxFastFire) != 0;
                            PlayerAttributes fireAttrs = _lp.Attributes;
                            if (fireAttrs != null)
                            {
                                if (fastFireOn)
                                {
                                    fireAttrs.FireIntervalScale = 0.5f;
                                }
                                else if (fireAttrs.FireIntervalScale < 0.9f)
                                {
                                    fireAttrs.FireIntervalScale = 1.0f;
                                }
                            }
                        }

                        {
                            bool backJumpOn = (packedAuxState & AuxBackJump) != 0;
                            try
                            {
                                if (backJumpOn)
                                {
                                    COW.GameVarDef.EnableAccelerationOnFalling = false;
                                    COW.GameVarDef.EnableLowFallingSwapWeapon = true;
                                }
                                else
                                {
                                    COW.GameVarDef.EnableAccelerationOnFalling = true;
                                    COW.GameVarDef.EnableLowFallingSwapWeapon = false;
                                }
                            }
                            catch (Exception) { }
                        }

                        {
                            // SetControlEnabled inline — cross-assembly calls not allowed in IFix woven methods
                            bool ghostControlOn = (packedAuxState & AuxGhostControl) != 0;
                            if (ghostControlOn)
                            {
                                if (GameObject.Find("__esp_ghost_control") == null)
                                {
                                    GameObject _gc = new GameObject("__esp_ghost_control");
                                    UnityEngine.Object.DontDestroyOnLoad(_gc);
                                }
                            }
                            else
                            {
                                GameObject _gcOff = GameObject.Find("__esp_ghost_control");
                                if (_gcOff != null) { _gcOff.name = "__esp_ghost_control_off"; UnityEngine.Object.Destroy(_gcOff); }
                                GameObject _gaOff = GameObject.Find("__esp_ghost_active");
                                if (_gaOff != null) { _gaOff.name = "__esp_ghost_active_off"; UnityEngine.Object.Destroy(_gaOff); }
                                GameObject _gnOff = GameObject.Find("__esp_ghost_anchor");
                                if (_gnOff != null) { _gnOff.name = "__esp_ghost_anchor_off"; UnityEngine.Object.Destroy(_gnOff); }
                                GameObject _gdOff = GameObject.Find("__esp_ghost_drag");
                                if (_gdOff != null) { _gdOff.name = "__esp_ghost_drag_off"; UnityEngine.Object.Destroy(_gdOff); }
                                GameObject _guOff = GameObject.Find("__esp_ghost_ui_state");
                                if (_guOff != null) { _guOff.name = "__esp_ghost_ui_state_off"; UnityEngine.Object.Destroy(_guOff); }
                            }
                        }

                        {
                            int r8 = (int)PlayerPrefs.GetFloat("esp_r8", 0f);
                            PlayerAttributes rAttrs = _lp.Attributes;
                            if (rAttrs != null)
                            {
                                bool fastReviveOn = (r8 & R8FastRevive) != 0;
                                if (fastReviveOn) { rAttrs.RescureRate = 5.0f; }
                                else if (rAttrs.RescureRate > 2.0f) { rAttrs.RescureRate = 1.0f; }
                                bool skillCDOn = (r8 & R8SkillCD) != 0;
                                rAttrs.ActiveSkillCdReduction = skillCDOn ? 0.9f : 0f;
                                rAttrs.PetSkillCDReduction = skillCDOn ? 0.9f : 0f;
                            }

                            {
                                bool chamsOn = (r8 & R8Chams) != 0;
                                try
                                {
                                    if (chamsOn)
                                    {
                                        COW.GameVarDef.EnableShowPlayerOutline = true;
                                        COW.GameVarDef.ShowPlayerOutlineMaxDistance = 500u;
                                        COW.GameVarDef.ShowPlayerOutlineColor = 0xFFFF0000u;
                                        COW.GameVarDef.ShowPlayerOutlineWidth = 3f;
                                        COW.GameVarDef.PCOBOutlineSolid = true;
                                    }
                                    else
                                    {
                                        COW.GameVarDef.EnableShowPlayerOutline = false;
                                        COW.GameVarDef.PCOBOutlineSolid = false;
                                    }
                                }
                                catch (Exception) { }
                            }

                            {
                                bool fastLootOn = (r8 & R8FastLoot) != 0;
                                try
                                {
                                    if (fastLootOn)
                                    {
                                        COW.GameVarDef.AutoPickupPoolOptEnabled = true;
                                        COW.GameVarDef.AutoPickupInvokeOptEnabled = true;
                                        COW.GameVarDef.AutoPickUpSortAttachmentEnable = true;
                                        COW.GameVarDef.EnableBackgroundCacheAutoPickupWeapon = true;
                                        COW.GameVarDef.EnableBackgroundCacheAutoPickupFppWeapon = true;
                                    }
                                }
                                catch (Exception) { }
                            }

                            {
                                bool camHackOn = (r8 & R8CamHack) != 0;
                                try
                                {
                                    if (camHackOn)
                                    {
                                        COW.GameVarDef.CameraShakeCabinFactor = 1.0f;
                                        COW.GameVarDef.CameraBackOffectLowerCutAngle = 1.0f;
                                        COW.GameVarDef.CameraBackOffectLowerAngleDist = 1.0f;
                                        COW.GameVarDef.CameraUpOffectLowerAngleDist = 1.0f;
                                        COW.GameVarDef.CameraUpOffectLowerHitDrawBackDist = 1.0f;
                                    }
                                }
                                catch (Exception) { }
                            }

                            {
                                bool unlockFpsOn = (r8 & R8UnlockFps) != 0;
                                try
                                {
                                    if (unlockFpsOn)
                                    {
                                        if (Application.targetFrameRate != 120)
                                        {
                                            Application.targetFrameRate = 120;
                                            GameFacade.SetFrameRate(COW.EHighFPS.HighFPS120);
                                        }
                                    }
                                    else if (Application.targetFrameRate > 60)
                                    {
                                        Application.targetFrameRate = 60;
                                        GameFacade.SetFrameRate(COW.EHighFPS.HighFPS);
                                    }
                                }
                                catch (Exception) { }
                            }

                            {
                                bool noFogOn = (r8 & R8NoFog) != 0;
                                try
                                {
                                    if (noFogOn)
                                    {
                                        RenderSettings.fog = false;
                                        CameraControllerManager noFogCam = GameFacade.CurrentCameraControllerManager();
                                        if (noFogCam != null) noFogCam.SetFarClipPlane(1000f);
                                    }
                                    else if (!RenderSettings.fog)
                                    {
                                        RenderSettings.fog = true;
                                        CameraControllerManager noFogCam = GameFacade.CurrentCameraControllerManager();
                                        if (noFogCam != null) noFogCam.ReSetFarClipPlane();
                                    }
                                }
                                catch (Exception) { }
                            }

                            {
                                bool fastCrouchOn = (r8 & R8FastCrouch) != 0;
                                try { COW.GameVarDef.CanCrouchingRunFast = fastCrouchOn; }
                                catch (Exception) { }
                            }
                        }

                        {
                            if ((state & AimSystemEnabled) != 0 && (state & AimEnabled) == 0
                                && Input.touchCount >= 2)
                            {
                                Camera _cam = Camera.main;
                                {{MATCH_TYPE}} _match = GameFacade.CurrentMatch();
                                IList _pl = _match == null ? null : _match.{{MATCH_PLAYERS_METHOD}}();
                                if (_cam != null && _pl != null)
                                {
                                    int _mode = (state & AimModeMask) >> AimModeShift;
                                    int _fovRaw = (packedAuxState >> AuxFovRadiusShift) & 0xFF;
                                    float _fovR = _fovRaw >= 10 ? (float)_fovRaw * 2f : 200f;
                                    float _bestSc = _fovR * _fovR;
                                    Player _bt = null; Collider _bc = null; Vector3 _bp = Vector3.zero;
                                    int _pcnt = _pl.Count; if (_pcnt > 64) _pcnt = 64;
                                    for (int _i = 0; _i < _pcnt; _i++)
                                    {
                                        try
                                        {
                                            Player _c = _pl[_i] as Player;
                                            if (_c == null || _c.gameObject == null
                                                || !_c.gameObject.activeInHierarchy
                                                || _c.IsLocalPlayer() || _c.CurHP <= 0
                                                || _c.IsLocalTeammate(false)
                                                || _c.IsLocalTeammate(true)
                                                || ((_c.IsDieing || _c.IsKnockedDownBleed) && (state & AimSkipDowned) != 0)) continue;
                                            Transform _cr = _c.RootTransform;
                                            if (_cr == null) continue;
                                            Vector3 _tp; Collider _tc = null;
                                            if (_mode == 1)
                                            {
                                                Transform _h = _c.GetHeadTF();
                                                if (_h == null) continue;
                                                _tp = _h.position;
                                                _tc = _c.HeadCollider;
                                                if (_tc == null) _tc = (Collider)_h.GetComponent(typeof(Collider));
                                            }
                                            else
                                            {
                                                _tp = _cr.position + new Vector3(0f, 0.9f, 0f);
                                                _tc = (Collider)_cr.GetComponent("CapsuleCollider");
                                                if (_tc == null) _tc = _c.HeadCollider;
                                            }
                                            if (_tc == null) continue;
                                            Vector3 _sv = _cam.WorldToScreenPoint(_tp);
                                            if (_sv.z <= 1f || float.IsNaN(_sv.x) || float.IsNaN(_sv.y)
                                                || float.IsInfinity(_sv.x) || float.IsInfinity(_sv.y)) continue;
                                            float _dx = _sv.x - (float)Screen.width * 0.5f;
                                            float _dy = _sv.y - (float)Screen.height * 0.5f;
                                            float _sc = _dx * _dx + _dy * _dy;
                                            if (float.IsNaN(_sc) || float.IsInfinity(_sc) || _sc >= _bestSc) continue;
                                            _bestSc = _sc; _bt = _c; _bc = _tc; _bp = _tp;
                                        }
                                        catch (Exception) { }
                                    }
                                    if (_bt != null && _bc != null)
                                    {
                                        try
                                        {
                                            _bt.EspLockedAimingCollider = _bc;
                                            Vector3 _dir = _bp - _lp.AimStartPostion;
                                            float _dlen = _dir.sqrMagnitude;
                                            if (_dlen > 0.0001f && !float.IsNaN(_dlen) && !float.IsInfinity(_dlen))
                                            {
                                                Quaternion _tgt = Quaternion.LookRotation(_dir);
                                                Quaternion _cur = _lp.GetAimRotation();
                                                float _fd = _dt > 0f && _dt < 0.1f ? _dt : 0.016f;
                                                float _bl = 1f - Mathf.Exp(-64f * _fd);
                                                _bl = Mathf.Clamp01(_bl);
                                                _lp.SetAimRotation(Quaternion.Slerp(_cur, _tgt, _bl), false);
                                            }
                                        }
                                        catch (Exception) { }
                                    }
                                }
                            }
                        }

                        if ((auxState & AuxFastParachute) != 0)
                        {
                            bool skySurfing = _lp.IsSkySurfing;
                            bool skyDiving = _lp.IsSkyDiving;
                            bool parachuting = _lp.IsParachuting;
                            if (skySurfing)
                            {
                                _lp.RequestSkyDiving();
                            }
                            else if (skyDiving || parachuting)
                            {
                                CharacterController controller = _lp.CharacterController;
                                if (controller != null && controller.enabled)
                                {
                                    RaycastHit groundHit;
                                    Vector3 origin = controller.transform.position;
                                    bool foundGround = Physics.Raycast(
                                        origin,
                                        new Vector3(0f, -1f, 0f),
                                        out groundHit,
                                        2048f,
                                        -5,
                                        QueryTriggerInteraction.Ignore);
                                    if (foundGround)
                                    {
                                        bool landed = controller.isGrounded;
                                        float groundDistance = groundHit.distance;
                                        if (!landed && groundDistance > 0f)
                                        {
                                            float contactPadding = controller.skinWidth;
                                            if (contactPadding < 0.05f)
                                            {
                                                contactPadding = 0.05f;
                                            }
                                            float moveDistance = groundDistance + contactPadding;
                                            float frameStep = _dt * 768f;
                                            if (frameStep < 0.5f)
                                            {
                                                frameStep = 0.5f;
                                            }
                                            else if (frameStep > 32f)
                                            {
                                                frameStep = 32f;
                                            }
                                            if (moveDistance > frameStep)
                                            {
                                                moveDistance = frameStep;
                                            }
                                            CollisionFlags collision =
                                                controller.Move(new Vector3(0f, -moveDistance, 0f));
                                            landed = (collision & CollisionFlags.Below) != 0
                                                || controller.isGrounded;
                                        }
                                        if (landed)
                                        {
                                            _lp.StopParachuting(true);
                                            _lp.OnLandFinsish();
                                        }
                                    }
                                }
                            }
                        }

                        packedAuxState = (packedAuxState & ~AuxMask) | (auxState & AuxMask);
                        self.{{SCENE_STATE_FIELD}} = new Vector2(
                            self.{{SCENE_STATE_FIELD}}.x,
                            -AuxStateMarker - (float)packedAuxState);
                    }
                }
            }
            catch (Exception) { }

            GUI.matrix = savedMatrix;
            GUI.color = savedColor;
        }

        public static {{AIM_INFO_TYPE}} SilentAim(Player self)
        {
            if (self == null)
            {
                return null;
            }
            {{AIM_INFO_TYPE}} info = self.{{PLAYER_AIM_INFO_FIELD}};
            if (info == null)
            {
                return info;
            }
            try
            {
                GameObject driver = GameObject.Find("__esp_driver");
            SceneEditBoxSelectTool menu = driver == null
                ? null
                : (SceneEditBoxSelectTool)driver.GetComponent(typeof(SceneEditBoxSelectTool));
            int state = menu == null ? 0 : (int)menu.{{SCENE_STATE_FIELD}}.x;

            // FakeDamage: works independently of SilentAim toggle
            float fdEnc = menu == null ? 0f : menu.{{SCENE_STATE_FIELD}}.y;
            if (fdEnc <= -AuxStateMarker)
            {
                int fdAux = (int)(-fdEnc - AuxStateMarker) & AuxMask;
                PlayerAttributes fdAttrs = self.Attributes;
                if (fdAttrs != null)
                {
                    if ((fdAux & AuxFakeDamage) != 0)
                    {
                        fdAttrs.BuffWeaponDamageScale = 10000.0f;
                        fdAttrs.DamageAdditionScale   = 10000.0f;
                        fdAttrs.ExecuteDamageScale    = 10000.0f;
                    }
                    else if (fdAttrs.BuffWeaponDamageScale > 10.0f)
                    {
                        fdAttrs.BuffWeaponDamageScale = 0f;
                        fdAttrs.DamageAdditionScale   = 0f;
                        fdAttrs.ExecuteDamageScale    = 0f;
                    }
                }
            }

            bool silentAimOn = (state & AimEnabled) != 0;
            bool aimFovOn    = (state & AimSystemEnabled) != 0;
            if (!silentAimOn && !aimFovOn)
            {
                return info;
            }

            // Read FOV values from full aux bits (no AuxMask cap)
            int silentFovPx  = 0;
            int aimFovPixels = 0;
            if (fdEnc <= -AuxStateMarker)
            {
                int fullAux  = (int)(-fdEnc - AuxStateMarker);
                silentFovPx  = ((fullAux >> AuxSilentFovShift) & 0xFF) * 2;
                aimFovPixels = ((fullAux >> AuxFovRadiusShift) & 0xFF) * 2;
            }

            bool aimAtHead;
            int fovFilterPx;
            if (silentAimOn)
            {
                aimAtHead   = true;
                fovFilterPx = silentFovPx;
            }
            else
            {
                int aimModeFov  = (state & AimModeMask) >> AimModeShift;
                int headRateFov = ((state & HeadRateMask) >> HeadRateShift) * 25;
                aimAtHead   = aimModeFov == 1
                    || (aimModeFov == 2 && UnityEngine.Random.Range(0, 100) < headRateFov);
                fovFilterPx = aimFovPixels;
            }

            {{MATCH_TYPE}} match = GameFacade.CurrentMatch();
            IList players = match == null ? null : match.{{MATCH_PLAYERS_METHOD}}();
            Camera camera = Camera.main;
            if (players == null || camera == null)
            {
                return info;
            }

            Vector3 aimStart = self.AimStartPostion;
            float bestScore = float.MaxValue;
            Collider bestCollider = null;
            Vector3 bestPosition = Vector3.zero;

            for (int index = 0; index < players.Count; index++)
            {
                Player candidate = players[index] as Player;
                GameObject candidateObject = candidate == null ? null : candidate.gameObject;
                if (candidateObject == null || !candidateObject.activeInHierarchy
                    || candidate.IsLocalPlayer()
                    || candidate.CurHP <= 0
                    || candidate.IsLocalTeammate(false)
                    || candidate.IsLocalTeammate(true)
                    || ((candidate.IsDieing || candidate.IsKnockedDownBleed) && (state & AimSkipDowned) != 0))
                {
                    continue;
                }

                Transform root = candidate.RootTransform;
                Transform head = candidate.GetHeadTF();
                Collider headCollider = candidate.HeadCollider;
                if (root == null)
                {
                    continue;
                }

                Vector3 hitPosition;
                Collider hitCollider;
                if (aimAtHead)
                {
                    if (head == null || headCollider == null)
                    {
                        continue;
                    }
                    hitPosition = head.position;
                    hitCollider = headCollider;
                }
                else
                {
                    hitPosition = root.position + new Vector3(0f, 0.9f, 0f);
                    hitCollider = (Collider)root.GetComponent("CapsuleCollider");
                    if (hitCollider == null)
                    {
                        if (head == null || headCollider == null)
                        {
                            continue;
                        }
                        hitCollider = headCollider;
                    }
                }

                if (Vector3.Distance(hitPosition, aimStart) > 150f)
                {
                    continue;
                }
                Vector3 screen = camera.WorldToScreenPoint(hitPosition);
                if (screen.z <= 1f
                    || float.IsNaN(screen.x) || float.IsNaN(screen.y)
                    || float.IsNaN(screen.z) || float.IsInfinity(screen.x)
                    || float.IsInfinity(screen.y) || float.IsInfinity(screen.z))
                {
                    continue;
                }
                float dx = screen.x - (float)Screen.width * 0.5f;
                float dy = screen.y - (float)Screen.height * 0.5f;
                float score = dx * dx + dy * dy;
                if (float.IsNaN(score) || float.IsInfinity(score)
                    || score >= bestScore)
                {
                    continue;
                }
                if (fovFilterPx > 0 && score > (float)(fovFilterPx * fovFilterPx))
                {
                    continue;
                }

                bestScore = score;
                bestCollider = hitCollider;
                bestPosition = hitPosition;
            }

            if (bestCollider != null)
            {
                if (silentAimOn)
                {
                    // Silent Aim: snap bullet direction instantly
                    Vector3 rayDirection = Vector3.Normalize(bestPosition - aimStart);
                    info.{{AIM_GAME_OBJECT_FIELD}} = bestCollider.gameObject;
                    info.{{AIM_COLLIDER_FIELD}} = bestCollider;
                    info.{{AIM_HIT_POSITION_FIELD}} = bestPosition;
                    info.{{AIM_TRACE_POSITION_FIELD}} = bestPosition;
                    info.{{AIM_DIRECTION_FIELD}} = rayDirection;
                    info.{{AIM_ORIGIN_FIELD}} = aimStart;
                    info.{{AIM_SECONDARY_ORIGIN_FIELD}} = aimStart;
                    info.{{AIM_HIT_TYPE_FIELD}} = ({{AIM_HIT_TYPE}})1;
                    info.{{AIM_FLAG_A_FIELD}} = false;
                    info.{{AIM_FLAG_B_FIELD}} = false;
                    info.{{AIM_SHORT_FIELD}} = (short)0;
                }
                else
                {
                    // Aim FOV: game's built-in aim assist toward locked collider
                    self.EspLockedAimingCollider = bestCollider;
                }
            }
                return info;
            }
            catch (Exception)
            {
                return info;
            }
        }

        public static bool AimSystem(Player self)
        {
            if (self == null)
            {
                return false;
            }
            bool original = false;
            try
            {
                original = self.EspBaseIsMovableEntity();
                GameObject driver = GameObject.Find("__esp_driver");
            SceneEditBoxSelectTool menu = driver == null
                ? null
                : (SceneEditBoxSelectTool)driver.GetComponent(typeof(SceneEditBoxSelectTool));
            int state = menu == null ? 0 : (int)menu.{{SCENE_STATE_FIELD}}.x;
            if ((state & AimSystemEnabled) == 0 || (state & AimEnabled) != 0
                || self.gameObject == null || !self.gameObject.activeInHierarchy
                || self.IsLocalPlayer() || self.CurHP <= 0
                || self.IsLocalTeammate(false) || self.IsLocalTeammate(true)
                || ((self.IsDieing || self.IsKnockedDownBleed) && (state & AimSkipDowned) != 0))
            {
                return original;
            }

            int aimModeFov = (state & AimModeMask) >> AimModeShift;
            int headRateFov = ((state & HeadRateMask) >> HeadRateShift) * 25;
            bool fovAimAtHead = aimModeFov == 1
                || (aimModeFov == 2 && UnityEngine.Random.Range(0, 100) < headRateFov);
            Collider targetCollider = null;
            if (fovAimAtHead)
            {
                targetCollider = self.HeadCollider;
            }
            else
            {
                Transform neck = self.NeckBone;
                IList fireColliders = self.FireColliders;
                float nearestDistance = 10000f;
                if (neck != null && fireColliders != null)
                {
                    for (int index = 0; index < fireColliders.Count; index++)
                    {
                        Collider candidateCollider = fireColliders[index] as Collider;
                        if (candidateCollider == null)
                        {
                            continue;
                        }

                        float distance = Vector3.Distance(
                            candidateCollider.transform.position, neck.position);
                        if (distance < nearestDistance)
                        {
                            nearestDistance = distance;
                            targetCollider = candidateCollider;
                        }
                    }
                }

                if (targetCollider == null && neck != null)
                {
                    targetCollider = (Collider)neck.GetComponent(typeof(Collider));
                }
                if (targetCollider == null)
                {
                    Transform root = self.RootTransform;
                    targetCollider = root == null
                        ? null
                        : (Collider)root.GetComponent("CapsuleCollider");
                }
                if (targetCollider == null)
                {
                    targetCollider = self.HeadCollider;
                }
            }

            if (targetCollider == null)
            {
                return original;
            }

            float fovEnc = menu == null ? 0f : menu.{{SCENE_STATE_FIELD}}.y;
            int fovPackedAim = fovEnc <= -AuxStateMarker
                ? (int)(-fovEnc - AuxStateMarker) : 0;
            float fovRadiusAim = (float)((fovPackedAim >> AuxFovRadiusShift) & 0xFF) * 2f;
            if (fovRadiusAim > 0f)
            {
                Camera camAim = Camera.main;
                if (camAim != null)
                {
                    Vector3 screenPosAim = camAim.WorldToScreenPoint(
                        targetCollider.transform.position);
                    if (screenPosAim.z > 0f)
                    {
                        float dxAim = screenPosAim.x - (float)Screen.width * 0.5f;
                        float dyAim = screenPosAim.y - (float)Screen.height * 0.5f;
                        if (dxAim * dxAim + dyAim * dyAim > fovRadiusAim * fovRadiusAim)
                        {
                            return original;
                        }
                    }
                }
            }

            self.EspLockedAimingCollider = targetCollider;
                return original;
            }
            catch (Exception)
            {
                return original;
            }
        }

        public static float ScatterRate(PlayerAttributes self)
        {
            if (self == null)
            {
                return 0f;
            }
            try
            {
                GameObject driver = GameObject.Find("__esp_driver");
            SceneEditBoxSelectTool menu = driver == null
                ? null
                : (SceneEditBoxSelectTool)driver.GetComponent(typeof(SceneEditBoxSelectTool));
            int state = menu == null ? 0 : (int)menu.{{SCENE_STATE_FIELD}}.x;
            if ((state & NoRecoil) != 0)
            {
                return 0f;
            }
                return 1f;
            }
            catch (Exception)
            {
                return 1f;
            }
        }
    }
}
