#pragma once
// Engine-independent code. This is NOT a CS2/Metamod physics binding.
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>
#include <optional>
#include <stdexcept>

namespace deathlab {
struct Vec3 { double x{}, y{}, z{}; };
inline Vec3 operator+(Vec3 a, Vec3 b) { return {a.x+b.x,a.y+b.y,a.z+b.z}; }
inline Vec3 operator-(Vec3 a, Vec3 b) { return {a.x-b.x,a.y-b.y,a.z-b.z}; }
inline Vec3 operator*(Vec3 a, double b) { return {a.x*b,a.y*b,a.z*b}; }
inline bool finite(Vec3 a) {
    return std::isfinite(a.x)&&std::isfinite(a.y)&&std::isfinite(a.z);
}
inline double length(Vec3 a) { return std::hypot(a.x,a.y,a.z); }
inline Vec3 cross(Vec3 a, Vec3 b) {
    return {a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x};
}
// All arguments use the adapter's documented consistent unit system.
// omega: radians/second, point_from_com: displacement from center of mass.
inline Vec3 velocity_at_point(Vec3 com_velocity, Vec3 omega, Vec3 point_from_com) {
    if (!finite(com_velocity)||!finite(omega)||!finite(point_from_com))
        throw std::invalid_argument("non-finite velocity input");
    auto v=com_velocity+cross(omega,point_from_com);
    if (!finite(v)) throw std::overflow_error("velocity overflow");
    return v;
}
// J = mass * delta_v. For whole-rig COM budgeting use total rig mass.
// A separate per-part cap can be applied before the adapter commits impulses.
inline Vec3 cap_impulse(Vec3 j, double mass, double max_delta_v) {
    if (!finite(j)||!std::isfinite(mass)||mass<=0||
        !std::isfinite(max_delta_v)||max_delta_v<0)
        throw std::invalid_argument("invalid impulse arguments");
    const double cap=mass*max_delta_v;
    const double mag=length(j);
    if (!std::isfinite(cap)||!std::isfinite(mag))
        throw std::overflow_error("impulse overflow");
    return mag>cap && mag>0 ? j*(cap/mag) : j;
}
struct Load { double force{}; double torque{}; };
inline bool valid(Load x) {
    return std::isfinite(x.force)&&std::isfinite(x.torque)&&x.force>=0&&x.torque>=0;
}
// Only call this when the adapter has verified that the solver values are
// per-step linear/angular impulses, NOT values that are already forces/torques.
inline Load load_from_impulses(Vec3 linear, Vec3 angular, double physics_dt) {
    if (!finite(linear)||!finite(angular)||!std::isfinite(physics_dt)||physics_dt<=0)
        throw std::invalid_argument("invalid solver impulse sample");
    Load l{length(linear)/physics_dt,length(angular)/physics_dt};
    if (!valid(l)) throw std::overflow_error("constraint load overflow");
    return l;
}
struct Limits { double force{}; double torque{}; };
struct BreakConfig {
    Limits intact;
    Limits tether;
    double direct_detach_ratio{2.0};
    std::uint32_t sustained_steps{2};
};
enum class Stage : std::uint8_t { Intact, Tethered, Detached };
struct Proposal {
    std::uint64_t generation{};
    Stage from{}, to{};
};
// A proposal does NOT mean physics has changed. Commit it only after the
// engine-side constraint transaction succeeds outside the physics callback.
class JointState {
public:
    explicit JointState(BreakConfig c) : cfg_(c) {
        auto limits_ok=[](Limits l){
            return std::isfinite(l.force)&&std::isfinite(l.torque)&&
                   l.force>0&&l.torque>0;
        };
        if (!limits_ok(c.intact)||!limits_ok(c.tether)||
            !std::isfinite(c.direct_detach_ratio)||c.direct_detach_ratio<=1||
            c.sustained_steps==0)
            throw std::invalid_argument("invalid break configuration");
    }
    Stage stage() const { return stage_; }
    std::optional<Proposal> observe(std::uint64_t physics_step,
                                    std::optional<Load> load) {
        if (last_step_ && physics_step<=*last_step_) return std::nullopt;
        const bool contiguous=!last_step_ ||
            (*last_step_ != std::numeric_limits<std::uint64_t>::max() &&
             physics_step==*last_step_+1);
        last_step_=physics_step;
        if (!contiguous) overload_count_=0;
        if (stage_==Stage::Detached) return std::nullopt;
        if (!load || !valid(*load)) { overload_count_=0; return std::nullopt; }
        const Limits lim=stage_==Stage::Intact?cfg_.intact:cfg_.tether;
        const double severity=std::max(load->force/lim.force,load->torque/lim.torque);
        if (severity>=cfg_.direct_detach_ratio)
            return Proposal{generation_,stage_,Stage::Detached};
        if (severity<1.0) { overload_count_=0; return std::nullopt; }
        if (overload_count_<cfg_.sustained_steps) ++overload_count_;
        if (overload_count_<cfg_.sustained_steps) return std::nullopt;
        return Proposal{generation_,stage_,
                        stage_==Stage::Intact?Stage::Tethered:Stage::Detached};
    }
    bool commit(const Proposal& p) {
        if (p.generation!=generation_ || p.from!=stage_ || stage_==Stage::Detached)
            return false;
        const bool legal=(stage_==Stage::Intact &&
                          (p.to==Stage::Tethered||p.to==Stage::Detached))||
                         (stage_==Stage::Tethered && p.to==Stage::Detached);
        if (!legal) return false;
        stage_=p.to;
        ++generation_;
        overload_count_=0;
        return true;
    }
private:
    BreakConfig cfg_;
    Stage stage_{Stage::Intact};
    std::uint64_t generation_{};
    std::uint32_t overload_count_{};
    std::optional<std::uint64_t> last_step_;
};
} // namespace deathlab
