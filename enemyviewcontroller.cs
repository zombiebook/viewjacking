using System;
using System.Collections.Generic;
using UnityEngine;

namespace enemyviewjack
{
    // Duckov 로더가 찾는 엔트리포인트: enemyviewjack.ModBehaviour
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        protected override void OnAfterSetup()
        {
            try
            {
                Debug.Log("[EnemyViewJack] OnAfterSetup 호출됨");

                var go = new GameObject("EnemyViewJackRoot");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<EnemyViewJackController>();

                Debug.Log("[EnemyViewJack] EnemyViewJackController 추가 완료");
            }
            catch (Exception ex)
            {
                Debug.Log("[EnemyViewJack] 초기화 예외: " + ex);
            }
        }
    }

    // 관전 컨트롤러:
    // - 휠로 토글
    // - 관전 중 A/D로 대상 변경
    // - 화면 해킹 노이즈 + 스캔라인
    // - 관전 중 플레이어 위치/속도 고정 (움직이지 않게)
    public class EnemyViewJackController : MonoBehaviour
    {
        private static EnemyViewJackController _instance;

        private bool _isJacking;

        private readonly List<CharacterMainControl> _targets = new List<CharacterMainControl>();
        private int _currentIndex = -1;
        private CharacterMainControl _currentTarget;

        private float _nextSwitchTime;
        private const float SWITCH_COOLDOWN = 0.15f;

        // 마우스 상태 저장용
        private bool _prevCursorVisible;
        private CursorLockMode _prevCursorLock;

        // 🔴 해킹 노이즈 관련
        private Texture2D _noiseTex;
        private float _noiseAlpha;
        private float _noiseScrollX;
        private float _noiseScrollY;
        private float _scanlineY;
        private float _scanlineTimer;

        // 🔒 플레이어 위치/속도 고정용
        private CharacterMainControl _player;
        private Vector3 _playerFrozenPos;
        private Quaternion _playerFrozenRot;
        private Rigidbody _playerRb;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            _player = CharacterMainControl.Main;

            if (_player == null)
            {
                if (_isJacking)
                    StopJack();
                return;
            }

            if (!_isJacking)
            {
                // 휠 클릭으로 관전 시작
                if (Input.GetMouseButtonDown(2))
                    StartJack();
                return;
            }

            // 관전 중일 때 ESC/휠 → 종료
            if (Input.GetMouseButtonDown(2) || Input.GetKeyDown(KeyCode.Escape))
            {
                StopJack();
                return;
            }

            // 🔒 관전 중엔 플레이어 위치/속도 계속 되돌려서 "안 움직이게"
            FreezePlayer();

            // 대상이 사라지면 다른 애로 자동 전환 시도
            if (!IsValidTarget(_currentTarget))
            {
                if (!TrySelectAnother())
                {
                    StopJack();
                    return;
                }
            }

            // 카메라는 계속 현재 타깃을 따라가게
            if (GameCamera.Instance != null && _currentTarget != null)
                GameCamera.Instance.SetTarget(_currentTarget);

            // A / D 로 타깃 변경 (너무 빨리 안 바뀌게 쿨타임)
            if (Time.unscaledTime >= _nextSwitchTime)
            {
                if (Input.GetKeyDown(KeyCode.D))
                    SwitchTarget(+1);
                else if (Input.GetKeyDown(KeyCode.A))
                    SwitchTarget(-1);
            }

            // 🔴 재킹 중일 때 노이즈/스캔라인 애니메이션
            if (_isJacking)
            {
                // 기본 투명도는 살짝 숨쉬듯이 변동
                _noiseAlpha = 0.18f + 0.07f * Mathf.Sin(Time.unscaledTime * 8f);

                // UV 스크롤 → 화면이 미세하게 흐르는 느낌
                _noiseScrollX += Time.unscaledDeltaTime * 0.6f;
                _noiseScrollY += Time.unscaledDeltaTime * 0.3f;
                if (_noiseScrollX > 1f) _noiseScrollX -= 1f;
                if (_noiseScrollY > 1f) _noiseScrollY -= 1f;

                // 스캔라인 가로줄 위치 변경
                _scanlineTimer += Time.unscaledDeltaTime;
                if (_scanlineTimer > 0.07f)
                {
                    _scanlineTimer = 0f;
                    _scanlineY = UnityEngine.Random.value; // 0~1
                }
            }
        }

        // 화면 해킹 노이즈 + 스캔라인
        private void OnGUI()
        {
            if (!_isJacking)
                return;

            EnsureNoiseTexture();

            Color prev = GUI.color;

            float texW = _noiseTex.width;
            float texH = _noiseTex.height;

            // 1) 전체 화면에 흐르는 노이즈
            GUI.color = new Color(1f, 0.35f, 0.35f, _noiseAlpha);

            Rect uv = new Rect(
                _noiseScrollX,
                _noiseScrollY,
                Screen.width / texW,
                Screen.height / texH
            );

            GUI.DrawTextureWithTexCoords(
                new Rect(0f, 0f, Screen.width, Screen.height),
                _noiseTex,
                uv
            );

            // 2) 강한 스캔라인 한 줄 (가로줄이 튀는 느낌)
            float lineY = _scanlineY * Screen.height;
            float lineHeight = Screen.height * 0.03f; // 줄 두께

            GUI.color = new Color(1f, 0.1f, 0.1f, _noiseAlpha * 1.6f);

            GUI.DrawTextureWithTexCoords(
                new Rect(0f, lineY, Screen.width, lineHeight),
                _noiseTex,
                new Rect(_noiseScrollX * 2f, _noiseScrollY * 2f, Screen.width / texW, lineHeight / texH)
            );

            GUI.color = prev;
        }

        // 노이즈 텍스처 한 번만 생성
        private void EnsureNoiseTexture()
        {
            if (_noiseTex != null)
                return;

            const int w = 128;
            const int h = 128;

            _noiseTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            _noiseTex.wrapMode = TextureWrapMode.Repeat;
            _noiseTex.filterMode = FilterMode.Point;

            var colors = new Color32[w * h];
            var rand = new System.Random();

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // 붉은 계열 노이즈 + 가로방향 줄무늬 섞기
                    byte baseV = (byte)rand.Next(60, 190);
                    float stripe = (Mathf.PerlinNoise(0f, y * 0.25f) * 0.4f + 0.6f);
                    byte v = (byte)(baseV * stripe);

                    colors[y * w + x] = new Color32(v, 0, 0, 255);
                }
            }

            _noiseTex.SetPixels32(colors);
            _noiseTex.Apply();
        }

        private void StartJack()
        {
            if (GameCamera.Instance == null)
            {
                Debug.Log("[EnemyViewJack] GameCamera.Instance 없음");
                return;
            }

            _player = CharacterMainControl.Main;
            if (_player == null)
            {
                Debug.Log("[EnemyViewJack] 플레이어를 찾지 못함");
                return;
            }

            RebuildTargetList(_player);

            if (_targets.Count == 0)
            {
                Debug.Log("[EnemyViewJack] 재킹할 대상 없음");
                return;
            }

            _currentIndex = FindBestIndex(_player);
            if (_currentIndex < 0) _currentIndex = 0;
            _currentTarget = _targets[_currentIndex];

            // 마우스 숨기고 고정 → 관전 중에는 안 움직이는 느낌
            _prevCursorVisible = Cursor.visible;
            _prevCursorLock = Cursor.lockState;
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;

            // 🔒 플레이어 현재 위치/회전/리짓바디 저장
            _playerFrozenPos = _player.transform.position;
            _playerFrozenRot = _player.transform.rotation;
            _playerRb = _player.GetComponent<Rigidbody>();
            FreezePlayer(); // 한 번 바로 적용

            _isJacking = true;
            _nextSwitchTime = Time.unscaledTime + SWITCH_COOLDOWN;

            // 노이즈 초기화
            _noiseScrollX = 0f;
            _noiseScrollY = 0f;
            _scanlineY = 0.5f;
            _scanlineTimer = 0f;
            _noiseAlpha = 0.25f;

            Debug.Log("[EnemyViewJack] 재킹 시작: " + _currentTarget.name);
        }

        private void StopJack()
        {
            if (!_isJacking)
                return;

            _isJacking = false;

            // 카메라 다시 내 캐릭으로
            if (GameCamera.Instance != null && CharacterMainControl.Main != null)
                GameCamera.Instance.SetTarget(CharacterMainControl.Main);

            // 마우스 상태 복구
            Cursor.visible = _prevCursorVisible;
            Cursor.lockState = _prevCursorLock;

            // 플레이어 프리즈 해제 (이제 더 이상 덮어쓰지 않음)
            _playerRb = null;
            _player = null;

            _targets.Clear();
            _currentTarget = null;
            _currentIndex = -1;

            Debug.Log("[EnemyViewJack] 재킹 종료");
        }

        // 관전 중 플레이어 위치/속도를 계속 고정
        private void FreezePlayer()
        {
            if (_player == null)
                return;

            _player.transform.position = _playerFrozenPos;
            _player.transform.rotation = _playerFrozenRot;

            if (_playerRb != null)
            {
                _playerRb.velocity = Vector3.zero;
                _playerRb.angularVelocity = Vector3.zero;
            }
        }

        private void RebuildTargetList(CharacterMainControl player)
        {
            _targets.Clear();

            CharacterMainControl[] all = FindObjectsOfType<CharacterMainControl>();
            for (int i = 0; i < all.Length; i++)
            {
                CharacterMainControl c = all[i];
                if (c == null) continue;
                if (c == player) continue;                       // 자기 자신 제외
                if (!c.gameObject.activeInHierarchy) continue;   // 비활성 제외
                if (IsPet(c.transform)) continue;                // 펫 제외

                _targets.Add(c);
            }
        }

        private bool IsValidTarget(CharacterMainControl c)
        {
            if (c == null) return false;
            if (!c.gameObject.activeInHierarchy) return false;
            if (IsPet(c.transform)) return false;
            return true;
        }

        private bool TrySelectAnother()
        {
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                if (!IsValidTarget(_targets[i]))
                    _targets.RemoveAt(i);
            }

            if (_targets.Count == 0)
                return false;

            if (_currentIndex < 0 || _currentIndex >= _targets.Count)
                _currentIndex = 0;

            _currentTarget = _targets[_currentIndex];
            Debug.Log("[EnemyViewJack] 대상 자동 재선택: " + _currentTarget.name);
            return true;
        }

        private void SwitchTarget(int dir)
        {
            if (_targets.Count == 0)
                return;

            _currentIndex += dir;
            if (_currentIndex >= _targets.Count) _currentIndex = 0;
            if (_currentIndex < 0) _currentIndex = _targets.Count - 1;

            _currentTarget = _targets[_currentIndex];
            _nextSwitchTime = Time.unscaledTime + SWITCH_COOLDOWN;

            Debug.Log("[EnemyViewJack] 대상 변경: " + _currentTarget.name);
        }

        private int FindBestIndex(CharacterMainControl player)
        {
            Vector3 pPos = player.transform.position;
            Vector3 pFwd = player.transform.forward;

            float bestScore = float.NegativeInfinity;
            int bestIndex = -1;

            for (int i = 0; i < _targets.Count; i++)
            {
                CharacterMainControl c = _targets[i];
                if (c == null) continue;

                Vector3 to = c.transform.position - pPos;
                float dist = to.magnitude;
                if (dist < 0.5f || dist > 80f) continue;

                to /= dist;
                float dot = Vector3.Dot(pFwd, to);  // 정면이면 1, 뒤면 -1

                float score = dot * 2f - dist * 0.02f; // 정면 + 가까운 애 우선
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        // 강아지 / 펫 필터 – 루트 오브젝트 이름으로 대충 걸러냄
        private bool IsPet(Transform t)
        {
            if (t == null) return false;

            Transform root = t;
            while (root.parent != null)
                root = root.parent;

            string name = root.name;
            if (string.IsNullOrEmpty(name)) return false;

            string lower = name.ToLowerInvariant();
            if (lower.Contains("pet_template")) return true;
            if (lower.Contains("pet")) return true;

            return false;
        }
    }
}
