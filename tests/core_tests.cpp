#include "death_core.hpp"
#include <iostream>
#include <stdexcept>
#include <limits>
using namespace deathlab;
int checks=0;
void check(bool ok, const char* message) {
    ++checks; if(!ok) throw std::runtime_error(message);
}
void near(double a,double b,const char* message) { check(std::abs(a-b)<1e-9,message); }
template<class F> void invalid(F f,const char* message) {
    bool threw=false; try { f(); } catch(const std::invalid_argument&) { threw=true; }
    check(threw,message);
}
// Synthetic test units; these are NOT measured CS2 force/torque thresholds.
BreakConfig config() { return {{100.0,50.0},{20.0,10.0},2.0,2}; }
int main() {
    auto v=velocity_at_point({10,0,0},{0,0,2},{0,3,0});
    near(v.x,4,"point velocity x"); near(v.y,0,"point velocity y");
    // Symmetric split preserves linear momentum when COM offsets sum to zero.
    const auto a=velocity_at_point({10,2,0},{0,0,2},{0,3,0});
    const auto b=velocity_at_point({10,2,0},{0,0,2},{0,-3,0});
    near((a.x+b.x)/2,10,"split COM vx"); near((a.y+b.y)/2,2,"split COM vy");
    auto j=cap_impulse({30,40,0},10,2);
    near(length(j),20,"bounded impulse"); near(j.x,12,"impulse direction");
    near(length(cap_impulse({0,0,0},10,2)),0,"zero impulse");
    near(length(cap_impulse({1,0,0},10,2)),1,"small impulse unchanged");
    near(length(cap_impulse({1,0,0},10,0)),0,"zero budget");
    invalid([]{cap_impulse({1,0,0},0,2);},"reject zero mass");
    invalid([]{load_from_impulses({1,0,0},{0,0,0},0);},"reject zero dt");
    invalid([]{cap_impulse({std::numeric_limits<double>::quiet_NaN(),0,0},1,2);},
            "reject NaN impulse");
    const auto l=load_from_impulses({0.5,0,0},{0,0,0.125},0.01);
    near(l.force,50,"linear impulse/dt"); near(l.torque,12.5,"angular impulse/dt");
    auto bad=config(); bad.intact.force=0;
    invalid([&]{JointState ignored(bad);},"invalid config");

    JointState state(config());
    check(!state.observe(1,Load{110,0}),"first overload only");
    check(!state.observe(1,Load{110,0}),"duplicate step cannot advance");
    auto p=state.observe(2,Load{110,0});
    check(p.has_value() && p->to==Stage::Tethered,"sustained overload proposes tether");
    check(state.stage()==Stage::Intact,"proposal is not committed prematurely");
    check(state.commit(*p),"commit success");
    check(!state.commit(*p),"duplicate commit blocked");
    check(state.stage()==Stage::Tethered,"tether active");
    check(!state.observe(3,Load{21,0}),"new constraint has fresh counter");
    auto torn=state.observe(4,Load{21,0});
    check(torn.has_value() && torn->to==Stage::Detached,"tether overload");
    check(state.commit(*torn),"detach commit");
    check(!state.observe(5,Load{999,999}),"detached terminal");

    JointState severe(config());
    auto d=severe.observe(1,Load{200,0});
    check(d.has_value() && d->to==Stage::Detached,"severe load skips tether");
    check(severe.commit(*d),"direct detach succeeds");
    JointState torque(config());
    auto t=torque.observe(1,Load{0,100});
    check(t.has_value() && t->to==Stage::Detached,"torque-only failure");
    JointState gap(config());
    check(!gap.observe(1,Load{110,0}),"gap first");
    check(!gap.observe(3,Load{110,0}),"missing step resets consecutive overload");
    check(!gap.observe(4,std::nullopt),"unavailable sample does not break");
    check(!gap.observe(5,Load{110,0}),"restart after unavailable sample");
    check(!gap.observe(6,Load{-1,0}),"invalid load resets");
    check(!gap.observe(7,Load{110,0}),"restart after invalid sample");
    check(!gap.observe(8,Load{90,0}),"under threshold resets");
    check(!gap.observe(9,Load{110,0}),"restart after under threshold");
    check(gap.observe(10,Load{110,0}).has_value(),"second valid sample triggers");
    JointState rollback(config());
    auto pending=rollback.observe(1,Load{250,0});
    check(pending.has_value(),"prepare request");
    // Engine rejected constraint transaction. Caller deliberately does not commit.
    check(rollback.stage()==Stage::Intact,"engine failure leaves core state intact");
    auto retry=rollback.observe(2,Load{250,0});
    check(retry.has_value() && rollback.commit(*retry),"retry transaction");
    check(!rollback.commit(*pending),"stale proposal rejected");
    std::cout<<"PASS: "<<checks<<" engine-independent checks\n";
}
