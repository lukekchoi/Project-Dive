using System;
using System.Collections;

using Core;
using Helpers;
using Phys;
using Player;
using World;

using MyBox;
using UnityEditor;
using UnityEngine;
using VFX;

[RequireComponent(typeof(PlayerStateMachine))]
[RequireComponent(typeof(BoxCollider2D))]
public class PlayerActor : Actor, IFilterLoggerTarget {
    [SerializeField, AutoProperty(AutoPropertyMode.Parent)] private PlayerStateMachine _stateMachine;
    [SerializeField, AutoProperty(AutoPropertyMode.Parent)] private BoxCollider2D _collider;
    [SerializeField] private SpriteRenderer _sprite;
    [SerializeField] private Transform diggingParticlesLoc;
    [SerializeField] private Death _deathManager;

    private bool _hitWallCoroutineRunning;
    private float _hitWallPrevSpeed;
    private GameObject _dpInstance;

    public int Facing => _sprite.flipX ? -1 : 1;

    private void OnEnable()
    {
        Room.RoomTransitionEvent += OnRoomTransition;
        _stateMachine.OnPlayerRespawn += DisableDeathParticles;
    }

    private void OnDisable()
    {
        Room.RoomTransitionEvent -= OnRoomTransition;
        _stateMachine.OnPlayerRespawn -= DisableDeathParticles;
    }

    #region Movement
    public void UpdateMovementX(int moveDirection, int acceleration) {
        int targetVelocityX = moveDirection * PlayerCore.MoveSpeed;
        int maxSpeedChange = (int) (acceleration * Game.Instance.FixedDeltaTime);
        velocityX = Mathf.MoveTowards(velocityX, targetVelocityX, maxSpeedChange);
    }

    public void Land()
    {
        if (_dpInstance != null) {
            _dpInstance?.GetComponent<DrillingParticles>()?.Stop();
        } 
        velocityY = 0;
    }
    #endregion

    #region Jumping
    
    /// <summary>
    /// Function that bounces the player.
    /// </summary>
    /// <param name="jumpHeight"></param>
    public void Jump(int jumpHeight) {
        if (_dpInstance != null) {
            _dpInstance?.GetComponent<DrillingParticles>()?.Stop();
        } 
        velocityY = GetJumpSpeedFromHeight(jumpHeight);
    }

    private void _mechanicBounceVelocity(Vector2 v)
    {
        velocity = v;
    }

    public void DoubleJump(int jumpHeight, int moveDirection = 0)
    {
        Jump(jumpHeight);

        // If the player is trying to go in the opposite direction of their x velocity, instantly switch direction.
        if (moveDirection != 0 && moveDirection != Math.Sign(velocityX))
        {
            velocityX = 0;
        }
    }

    public void JumpCut()
    {
        if (velocityY > 0f)
        {
            velocityY *= PlayerCore.JumpCutMultiplier;
        }
    }
    #endregion

    #region Dive
    public void Dive()
    {
        velocityY = PlayerCore.DiveVelocity;
    }

    public void UpdateWhileDiving()
    {
        float oldYV = velocityY;
        if (FallVelocityExceedsMax())
        {
            velocityY += PlayerCore.DiveDeceleration;
        }
        else
        {
            Fall();
        }
    }

    public bool IsDiving()
    {
        return _stateMachine.IsOnState<PlayerStateMachine.Diving>();
    }
    #endregion

    #region Dogo
    public float Dogo() {
        float v = velocityX;
        velocityX = 0;
        SpawnDrillingParticles();
        return v;
    }

    public void DogoJump(int moveDirection, bool conserveMomentum, double oldXV) {
        if (!(moveDirection == 1 || moveDirection == -1)) {
            throw new ArgumentException(
            $"Cannot dogo jump in direction({moveDirection})"
            );
        }
        velocityX = moveDirection * PlayerCore.DogoJumpXV;
        if (conserveMomentum)
        {
            float addSpeed = PlayerCore.DogoJumpXV * PlayerCore.UltraSpeedMult;
            if (moveDirection == 1)
            {
                velocityX = (float)Math.Max(oldXV + addSpeed, PlayerCore.DogoJumpXV);
            }
            else if (moveDirection == -1)
            {
                velocityX = (float)Math.Min(oldXV - addSpeed, -PlayerCore.DogoJumpXV);
            }
        }

        velocityY = GetJumpSpeedFromHeight(PlayerCore.DogoJumpHeight);
    }
    
    public bool IsDogoJumping()
    {
        return _stateMachine.IsOnState<PlayerStateMachine.DogoJumping>();
    }
    
    public bool IsDogoing()
    {
        return _stateMachine.IsOnState<PlayerStateMachine.Dogoing>();
    }

    public void BallBounce(Vector2 direction)
    {
        if (direction.x != 0)
        {
            velocityX = Math.Sign(direction.x) * -150;
        }
    }
    #endregion

    public void SpawnDrillingParticles() {
        if (_dpInstance == null) {
            _dpInstance = Instantiate(PlayerCore._diggingParticles, diggingParticlesLoc);
            UpdateDogoParticleFacing(Facing);
        }
    }

    public bool IsDrilling() {
        return _stateMachine.UsingDrill;
    }

    public void Die(Vector3 diePos)
    {
        _deathManager.transform.position = diePos;
        _deathManager.SetParticlesActive(true);
        _deathManager.Reset();
        velocity = Vector2.zero;
        _stateMachine.OnDeath();
    }
    
    public void DisableDeathParticles() => _deathManager.SetParticlesActive(false);

    #region Actor Overrides
    public override bool Collidable() {
        return true;
    }

    public override bool OnCollide(PhysObj p, Vector2 direction) {
        if (p != this)
        {
            FilterLogger.Log(this, $"Player collided with object {p} from direction {direction}");
        }
        bool col = p.PlayerCollide(this, direction);
        if (col) {
            if (direction.y > 0) {
                BonkHead();
            }

            if (direction.x != 0) {
                HitWall((int)direction.x);
            }
        }

        return col;
    }

    public override bool PlayerCollide(PlayerActor p, Vector2 direction)
    {
        return false;     
    }

    public override bool IsGround(PhysObj whosAsking)
    {
        return false;
    }

    public override bool Squish(PhysObj p, Vector2 d)
    {
        if (OnCollide(p, d))
        {
            Debug.Log("Squish " + p);
            Die(transform.position);
        }
        return false;
    }
    #endregion

    public void BonkHead() {
        velocityY = Math.Min(10, velocityY);
    }

    private void HitWall(int direction) {
        if (!_hitWallCoroutineRunning) {
            _hitWallPrevSpeed = velocityX;
            velocityX = 0;
            _hitWallCoroutineRunning = true;
            StartCoroutine(HitWallLogic(direction));
        }
    }

    private IEnumerator HitWallLogic(int direction) {
        for (float t = 0; t < PlayerCore.CornerboostTimer; t += Game.Instance.FixedDeltaTime) {
            bool movingWithDir = Math.Sign(velocityX) == Math.Sign(direction) || velocityX == 0;
            if (!movingWithDir) {
                break;
            }
            bool stillNextToWall = CheckCollisions(
                Vector2.right * direction, 
                (physObj, d) => {
                return physObj != this && physObj.Collidable();
            });
            if (!stillNextToWall) {
                velocityX = _hitWallPrevSpeed * PlayerCore.CornerboostMultiplier;
                break;
            }
            yield return null;
        }
        _hitWallCoroutineRunning = false;
    }

    private void OnRoomTransition(Room roomEntering)
    {
        velocityX *= PlayerCore.RoomTransitionVCutX;
        velocityY *= PlayerCore.RoomTransistionVCutY;
    }

    private float GetJumpSpeedFromHeight(float jumpHeight)
    {
        return Mathf.Sqrt(-2f * GravityUp * jumpHeight);
    }
    
    public void UpdateDogoParticleFacing(int facing)
    {
        if (facing != 0 && _dpInstance != null)
        {
            Vector3 scale = _dpInstance.transform.localScale;
            _dpInstance.transform.localScale = new Vector3(facing, scale.y, scale.z);
        }
    }
    
    #if UNITY_EDITOR
    private void OnDrawGizmosSelected() {
        Handles.Label(new Vector3(0, 56, 0) , $"Velocity: <{velocityX}, {velocityY}>");
    }
    #endif

    public LogLevel GetLogLevel()
    {
        return LogLevel.Error;
    }
}