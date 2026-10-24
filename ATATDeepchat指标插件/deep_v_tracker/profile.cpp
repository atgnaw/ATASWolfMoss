// 文件职责：按柱内源遍历顺序合并成交量，重现 POC 与不包含 POC 本身的 70% 扩展计算。
// 保留零价回退、向下同量优先与稀疏价位停止细节。
#include "profile.h"

#include <algorithm>
#include <iterator>
#include <map>

namespace vtracker {
namespace {

constexpr double PROFILE_FRACTION = 0.7;

struct MergedProfile {
    std::map<std::int64_t, std::int64_t> volumeByPrice;
    std::int64_t totalVolume{};
    std::int64_t maximumVolume{};
    std::int64_t pointOfControl{};
};

MergedProfile mergeProfiles(std::span<const Bar* const> bars) {
    MergedProfile result;
    const auto append = [&result](const PriceLevel& level) {
        const auto found = result.volumeByPrice.find(level.priceTicks);
        const auto oldVolume = found == result.volumeByPrice.end() ? 0 : found->second;
        const auto volume = checkedAdd(oldVolume, level.totalVolume);
        result.volumeByPrice.insert_or_assign(level.priceTicks, volume);
        result.totalVolume = checkedAdd(result.totalVolume, level.totalVolume);

        // 源程序逐次刷新最大值，同量保留先达到最大值的价位，不能在最终 map 上重新选最大。
        if (volume > result.maximumVolume) {
            result.maximumVolume = volume;
            result.pointOfControl = level.priceTicks;
        }
    };

    for (const auto* bar : bars) {
        // PriceCollection.Values 从首个成交价向上，再向下枚举；不是全价位升序。
        const auto middle = std::lower_bound(
            bar->profile.begin(), bar->profile.end(), bar->openTicks, [](const auto& level, auto price) { return level.priceTicks < price; });
        std::for_each(middle, bar->profile.end(), append);
        std::for_each(std::make_reverse_iterator(middle), bar->profile.rend(), append);
    }

    return result;
}

ProfileArea expandProfile(const MergedProfile& profile) {
    auto upper = checkedAdd(profile.pointOfControl, 1);
    auto lower = checkedAdd(profile.pointOfControl, -1);
    std::int64_t accumulated{};

    if (!profile.volumeByPrice.empty()) {
        do {
            const auto above = profile.volumeByPrice.find(upper);
            const auto below = profile.volumeByPrice.find(lower);
            const bool hasAbove = above != profile.volumeByPrice.end();
            const bool hasBelow = below != profile.volumeByPrice.end();
            const auto aboveVolume = hasAbove ? above->second : 0;
            const auto belowVolume = hasBelow ? below->second : 0;
            if (hasAbove && (!hasBelow || aboveVolume > belowVolume)) {
                accumulated = checkedAdd(accumulated, aboveVolume);
                upper = checkedAdd(upper, 1);
            } else {
                // 两侧缺失仍执行下沿推进，再检查停止；保留源程序稀疏价位的非标准边界。
                accumulated = checkedAdd(accumulated, belowVolume);
                lower = checkedAdd(lower, -1);
            }

            // 扩展量从零起算，不先加入 POC 成交量；两侧同量选择下方。
            if (static_cast<double>(accumulated) >= static_cast<double>(profile.totalVolume) * PROFILE_FRACTION || !(hasAbove || hasBelow)) {
                break;
            }
        } while (true);
    }

    return {profile.pointOfControl, checkedAdd(lower, 1), checkedAdd(upper, -1)};
}

} // namespace

ProfileArea calculateProfileArea(std::span<const Bar* const> bars) {
    return expandProfile(mergeProfiles(bars));
}

} // namespace vtracker
