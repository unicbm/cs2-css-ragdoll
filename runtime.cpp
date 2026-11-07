#include "core/death_core.hpp"
#include <ISmmPlugin.h>
#include <array>
static_assert(METAMOD_PLAPI_VERSION >= 18, "Metamod plugin interface 18 or newer required");
#include <cstdio>

namespace
{
struct Point
{
    float x, y, z;
};
using Handle = uint32_t; // zero reserved for failure; engine handles include serial
struct Segment
{
    Point center, angles, pivot;
};
struct Ligament
{
    std::array<Handle, 3> joints{};
    std::array<Handle, 2> nodes{};
};
struct Bridge
{
    Handle (*body)(int agent, int part, Point center, Point angles, Point velocity);
    Handle (*joint)(Handle parent, Handle child, Point anchor, float yaw, int hinge);
    void (*remove)(Handle entity);
    Handle (*liver)(Handle child);
    void (*blood)(Handle body, const Point *contact, Point direction, int tier);
    int (*ligament)(Handle parent, Handle child, Ligament *out);
    int (*overstretched)(Handle parent, Handle child);
};
Bridge engine{};
bool loaded{};
constexpr deathlab::BreakConfig damageLimits{{40, 1}, {20, 1}, 2.5, 1};
struct Link
{
    Handle entity{};
    Ligament ligament{};
    float bulletLoad{};
    deathlab::JointState logic{damageLimits};
};
struct Rig
{
    int count{};
    double born{}, expires{};
    std::array<Handle, 11> bodies{};
    std::array<Link, 11> links{};
    Handle liver{};
};
std::array<Rig, 6> rigs;
uint64_t sequence{};
int limit = 6;
float lifetime = 20;
constexpr int parents[] = {-1, 0, 1, 1, 3, 1, 5, 0, 7, 0, 9};
void release(Ligament &ligament)
{
    for (auto joint : ligament.joints)
        if (joint) engine.remove(joint);
    for (auto node : ligament.nodes)
        if (node) engine.remove(node);
    ligament = {};
}
void clear(Rig &rig)
{
    for (auto &link : rig.links)
    {
        if (link.entity)
            engine.remove(link.entity);
        release(link.ligament);
    }
    for (auto body : rig.bodies)
        if (body)
            engine.remove(body);
    if (rig.liver)
        engine.remove(rig.liver);
    rig = Rig{};
}
int transition(Rig &rig, int child, float damage, bool spray = true)
{
    auto &link = rig.links[child];
    auto proposal = link.logic.observe(++sequence, deathlab::Load{damage, 0});
    if (!proposal)
        return 0;
    if (proposal->to == deathlab::Stage::Tethered)
    {
        Ligament pending{};
        const int result = engine.ligament(rig.bodies[parents[child]], rig.bodies[child], &pending);
        if (result == 0)
        {
            release(pending);
            return -1;
        }
        if (result == 2) // The wound ends have already separated beyond the strand's reach.
            proposal->to = deathlab::Stage::Detached;
        else
            link.ligament = pending;
    }
    else
        release(link.ligament);
    if (link.entity)
    {
        engine.remove(link.entity);
        link.entity = 0;
    }
    link.logic.commit(*proposal);
    link.bulletLoad = 0;
    if (rig.count == 11)
    {
        if (spray)
            engine.blood(rig.bodies[child], nullptr, {}, proposal->to == deathlab::Stage::Detached ? 2 : 1);
        if (child == 1 && proposal->to == deathlab::Stage::Detached)
            rig.liver = engine.liver(rig.bodies[child]);
    }
    return proposal->to == deathlab::Stage::Detached ? 2 : 1;
}
} // namespace
#define API extern "C" __declspec(dllexport)
API int hwr_bind_v8(Bridge callbacks)
{
    if (!loaded || engine.body || !callbacks.body || !callbacks.joint || !callbacks.remove || !callbacks.liver || !callbacks.blood || !callbacks.ligament || !callbacks.overstretched)
        return 0;
    engine = callbacks;
    return 1;
}
API void hwr_clear()
{
    for (auto &rig : rigs)
        if (rig.count)
            clear(rig);
}
API void hwr_unbind()
{
    hwr_clear();
    engine = {};
}
API int hwr_config(int capacity, float seconds)
{
    if (capacity < 1 || capacity > 6 || !std::isfinite(seconds) || seconds < 1 || seconds > 120)
        return 0;
    limit = capacity;
    lifetime = seconds;
    return 1;
}
API void hwr_sweep(double now)
{
    for (auto &rig : rigs)
    {
        if (rig.count && rig.expires <= now)
        {
            clear(rig);
            continue;
        }
        for (int i = 1; i < rig.count; ++i)
            if (rig.links[i].logic.stage() == deathlab::Stage::Tethered &&
                engine.overstretched(rig.bodies[parents[i]], rig.bodies[i]))
                transition(rig, i, 20);
    }
}
API int hwr_spawn(int agent, float yaw, Point velocity, int probe, double now, int hit, float damage, Point impact, int scatter,
                  const Segment *pose)
{
    if (!engine.body || !pose)
        return 0;
    int slot = 0;
    while (slot < limit && rigs[slot].count)
        ++slot;
    if (slot == limit)
        slot = static_cast<int>(std::min_element(rigs.begin(), rigs.begin()+limit,
            [](const Rig &a, const Rig &b) { return a.born < b.born; }) - rigs.begin());
    Rig pending;
    pending.count = probe ? 2 : 11;
    pending.born = now;
    pending.expires = now + lifetime;
    for (int i = 0; i < pending.count; ++i)
    {
        int parent = parents[i];
        auto v = velocity;
        if (hit >= 0 && hit < pending.count)
        {
            const auto a = pose[i].center, b = pose[hit].center;
            const float dx = a.x-b.x, dy = a.y-b.y, dz = a.z-b.z;
            const float distance = std::sqrt(dx*dx + dy*dy + dz*dz);
            // Carry the body along the shot, with a stronger kick near the wound.
            const float share = 0.35f + 0.65f / (1.0f + distance / 14.0f);
            v.x += impact.x * share;
            v.y += impact.y * share;
            v.z += impact.z * share;
            if (scatter && distance > 1.0f)
            {
                const float spread = 260.0f / distance;
                v.x += dx * spread;
                v.y += dy * spread;
                v.z += dz * spread;
            }
        }
        const Segment &segment = pose[i];
        pending.bodies[i] = engine.body(agent, i, segment.center, segment.angles, v);
        if (!pending.bodies[i])
        {
            clear(pending);
            return 0;
        }
        if (parent < 0)
            continue;
        pending.links[i].entity = engine.joint(pending.bodies[parent], pending.bodies[i], segment.pivot, yaw,
                                               i == 4 || i == 6 || i == 8 || i == 10);
        if (!pending.links[i].entity)
        {
            clear(pending);
            return 0;
        }
    }
    int stage = 0;
    if (hit > 0 && hit < pending.count && (stage = transition(pending, hit, damage, false)) < 0)
    {
        clear(pending);
        return 0;
    }
    if (!probe && hit >= 0 && hit < pending.count && (damage > 0 || impact.x || impact.y || impact.z))
        engine.blood(pending.bodies[hit], nullptr, impact, scatter ? 3 : std::max(1, stage));
    if (scatter && hit > 0 && hit < pending.count)
    {
        // Tear outward from the wound, keeping distant joints available for follow-up shots.
        for (int extra = 0; extra < 2; ++extra)
        {
            int nearest = -1;
            float best = 25.0f * 25.0f;
            for (int i = 1; i < pending.count; ++i)
            {
                if (i == hit || pending.links[i].logic.stage() == deathlab::Stage::Detached)
                    continue;
                const auto a = pose[i].pivot, b = pose[hit].center;
                const float x = a.x-b.x, y = a.y-b.y, z = a.z-b.z;
                const float squared = x*x + y*y + z*z;
                if (squared < best) { best = squared; nearest = i; }
            }
            if (nearest < 0) break;
            transition(pending, nearest, damage);
        }
    }
    if (rigs[slot].count)
        clear(rigs[slot]);
    rigs[slot] = pending;
    return slot + 1;
}
API int hwr_hit(int id, int child, float damage)
{
    if (id < 1 || id > 6 || child < 1 || child >= rigs[id - 1].count)
        return -1;
    return transition(rigs[id - 1], child, damage);
}
// Return the struck part index, or 11 for a strand that was cut and removed.
API int hwr_shot(Handle body, Point position, Point direction, double now)
{
    for (auto &rig : rigs)
    {
        if (rig.count != 11 || now <= rig.born || now >= rig.expires)
            continue;
        for (int i = 0; i < rig.count; ++i)
        {
            const auto &nodes = rig.links[i].ligament.nodes;
            if (body && (nodes[0] == body || nodes[1] == body))
            {
                transition(rig, i, 20, false);
                engine.blood(rig.bodies[i], &position, direction, 2);
                return 11;
            }
            if (rig.bodies[i] != body)
                continue;
            int stage = 0;
            const int joint = i == 0 ? 1 : i; // pelvis and chest share the waist
            auto &link = rig.links[joint];
            if (link.logic.stage() != deathlab::Stage::Detached)
            {
                link.bulletLoad += 16;
                stage = transition(rig, joint, link.bulletLoad, false);
            }
            engine.blood(body, &position, direction, std::max(1, stage));
            return i;
        }
    }
    return -1;
}
API int hwr_broken(Handle handle)
{
    if (!handle) return 0;
    for (auto &rig : rigs)
        for (int i = 1; i < rig.count; ++i)
        {
            const auto &link = rig.links[i];
            const auto &joints = link.ligament.joints;
            if (link.entity == handle || std::find(joints.begin(), joints.end(), handle) != joints.end())
            {
                int stage = transition(rig, i, 40);
                if (stage < 0)
                    clear(rig); // the engine has already destroyed the old joint
                return stage;
            }
        }
    return 0;
}
API int hwr_count()
{
    int n = 0;
    for (auto &rig : rigs)
        n += rig.count;
    return n;
}
class HappyWheel final : public ISmmPlugin
{
  public:
    bool Load(PluginId, ISmmAPI *, char *, size_t, bool) override
    {
        loaded = true;
        return true;
    }
    bool Unload(char *error, size_t size) override
    {
        if (engine.body)
        {
            std::snprintf(error, size, "Unload the CounterStrikeSharp happy-wheel-ragdoll plugin first");
            return false;
        }
        loaded = false;
        return true;
    }
    const char *GetAuthor() override
    {
        return "unicbm";
    }
    const char *GetName() override
    {
        return "happy-wheel-ragdoll";
    }
    const char *GetDescription() override
    {
        return "Segmented crash dummy physics";
    }
    const char *GetURL() override
    {
        return "https://github.com/unicbm/cs2-css-ragdoll";
    }
    const char *GetLicense() override
    {
        return "AGPL-3.0-only";
    }
    const char *GetVersion() override
    {
        return "0.8.1";
    }
    const char *GetDate() override
    {
        return __DATE__;
    }
    const char *GetLogTag() override
    {
        return "HappyWheel";
    }
};
HappyWheel plugin;
PLUGIN_EXPOSE(HappyWheel, plugin);
