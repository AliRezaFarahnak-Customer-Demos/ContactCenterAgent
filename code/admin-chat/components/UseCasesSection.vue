<script setup lang="ts">
interface CountryStat {
  code: string;
  name: string;
  flag: string;
  calls: number;
  messages: number;
}

interface CallStatsData {
  totalCalls: number;
  totalMessages: number;
  totalCountries: number;
  countries: CountryStat[];
  lastUpdated: string;
}

const stats = ref<CallStatsData | null>(null);
const statsLoading = ref(true);
const animatedCalls = ref(0);
const animatedMessages = ref(0);
const animatedCountries = ref(0);

async function fetchStats() {
  try {
    const data = await $fetch<CallStatsData>("/api/call-stats");
    stats.value = data;
  } catch (err) {
    console.warn("[CallStats] Failed to fetch stats:", err);
  } finally {
    statsLoading.value = false;
  }
}

function animateNumber(
  target: Ref<number>,
  end: number,
  duration: number = 1800,
) {
  const start = 0;
  const startTime = performance.now();
  function update(currentTime: number) {
    const elapsed = currentTime - startTime;
    const progress = Math.min(elapsed / duration, 1);
    const eased = 1 - Math.pow(1 - progress, 3);
    target.value = Math.round(start + (end - start) * eased);
    if (progress < 1) requestAnimationFrame(update);
  }
  requestAnimationFrame(update);
}

watch(stats, (s) => {
  if (s) {
    setTimeout(() => {
      animateNumber(animatedCalls, s.totalCalls);
      animateNumber(animatedMessages, s.totalMessages, 2200);
      animateNumber(animatedCountries, s.totalCountries, 1200);
    }, 200);
  }
});

const useCases = [
  {
    icon: "💊",
    title: "Clinical Trials",
    desc: "Collect patient symptoms daily across thousands of participants — zero manual follow-up.",
    color: "violet",
  },
  {
    icon: "📞",
    title: "24/7 Support",
    desc: "Always-on, always consistent. Handle surges without hiring. No hold music, ever.",
    color: "amber",
  },
  {
    icon: "💼",
    title: "Sales Qualification",
    desc: "Qualify inbound leads with a call within 60 seconds of form submit. Instant engagement.",
    color: "blue",
  },
  {
    icon: "🏥",
    title: "Patient Scheduling",
    desc: "500 check-up calls in an hour. Confirmations, reminders, rescheduling — all handled.",
    color: "emerald",
  },
  {
    icon: "🚨",
    title: "Emergency Alerts",
    desc: "Mass-notify thousands in minutes — evacuations, outages, weather warnings. Every second counts.",
    color: "red",
  },
  {
    icon: "�",
    title: "Debt Collection",
    desc: "Automated, compliant collection calls at scale. Consistent tone, higher contact rates, lower costs.",
    color: "purple",
  },
];

const colorClasses: Record<
  string,
  { bg: string; border: string; icon: string }
> = {
  violet: {
    bg: "bg-violet-50",
    border: "border-violet-100 hover:border-violet-200",
    icon: "bg-violet-100",
  },
  emerald: {
    bg: "bg-emerald-50",
    border: "border-emerald-100 hover:border-emerald-200",
    icon: "bg-emerald-100",
  },
  blue: {
    bg: "bg-blue-50",
    border: "border-blue-100 hover:border-blue-200",
    icon: "bg-blue-100",
  },
  amber: {
    bg: "bg-amber-50",
    border: "border-amber-100 hover:border-amber-200",
    icon: "bg-amber-100",
  },
  rose: {
    bg: "bg-rose-50",
    border: "border-rose-100 hover:border-rose-200",
    icon: "bg-rose-100",
  },
  cyan: {
    bg: "bg-cyan-50",
    border: "border-cyan-100 hover:border-cyan-200",
    icon: "bg-cyan-100",
  },
  red: {
    bg: "bg-red-50",
    border: "border-red-100 hover:border-red-200",
    icon: "bg-red-100",
  },
  purple: {
    bg: "bg-purple-50",
    border: "border-purple-100 hover:border-purple-200",
    icon: "bg-purple-100",
  },
  indigo: {
    bg: "bg-indigo-50",
    border: "border-indigo-100 hover:border-indigo-200",
    icon: "bg-indigo-100",
  },
  orange: {
    bg: "bg-orange-50",
    border: "border-orange-100 hover:border-orange-200",
    icon: "bg-orange-100",
  },
};

const visible = ref(false);

onMounted(() => {
  fetchStats();
  setTimeout(() => {
    visible.value = true;
  }, 600);
});
</script>

<template>
  <div class="w-full max-w-3xl mx-auto">
    <div
      class="text-center mb-4"
      :class="visible ? 'opacity-100 translate-y-0' : 'opacity-0 translate-y-3'"
      style="transition: all 0.6s ease-out 0.3s"
    >
      <h3
        class="text-3xl sm:text-4xl font-black text-zinc-900 tracking-tight uppercase"
      >
        AI Call Center
      </h3>
    </div>

    <!-- Use case cards -->
    <div class="grid grid-cols-2 sm:grid-cols-2 gap-2 sm:gap-3">
      <div
        v-for="(uc, idx) in useCases"
        :key="uc.title"
        class="group relative flex items-center sm:items-start gap-2 sm:gap-4 p-2.5 sm:p-4 rounded-xl border hover:shadow-lg transition-all duration-300 cursor-default"
        :class="[
          colorClasses[uc.color].bg,
          colorClasses[uc.color].border,
          visible ? 'opacity-100 translate-y-0' : 'opacity-0 translate-y-4',
        ]"
        :style="{
          transitionDelay: `${idx * 120 + 500}ms`,
          transitionProperty: 'all',
          transitionDuration: '0.5s',
          transitionTimingFunction: 'ease-out',
        }"
      >
        <div
          class="shrink-0 w-8 h-8 sm:w-10 sm:h-10 rounded-lg sm:rounded-xl flex items-center justify-center text-base sm:text-xl group-hover:scale-110 transition-transform duration-200"
          :class="colorClasses[uc.color].icon"
        >
          {{ uc.icon }}
        </div>
        <div class="min-w-0">
          <div class="text-xs sm:text-sm font-bold text-zinc-800">
            {{ uc.title }}
          </div>
          <div
            class="hidden sm:block text-xs text-zinc-500 leading-relaxed mt-0.5"
          >
            {{ uc.desc }}
          </div>
        </div>
      </div>

      <!-- Live stats card (full width, below the use cases) -->
      <div
        v-if="stats && (stats.totalCalls > 0 || stats.countries.length > 0)"
        class="col-span-2 p-4 rounded-xl border bg-zinc-50 border-zinc-200 hover:border-zinc-300 hover:shadow-lg transition-all duration-500 cursor-default"
        :class="
          visible ? 'opacity-100 translate-y-0' : 'opacity-0 translate-y-4'
        "
        :style="{ transitionDelay: '980ms' }"
      >
        <!-- Top row: stats numbers + live badge -->
        <div class="flex items-center gap-6 sm:mb-3">
          <div
            class="shrink-0 w-10 h-10 rounded-xl bg-zinc-100 flex items-center justify-center text-xl"
          >
            📊
          </div>
          <div>
            <div
              class="text-2xl sm:text-3xl font-black text-zinc-900 tabular-nums tracking-tight leading-tight"
            >
              {{ animatedCalls.toLocaleString() }}
            </div>
            <div
              class="text-[10px] text-zinc-400 font-semibold uppercase tracking-wider"
            >
              AI Calls
            </div>
          </div>
          <div class="w-px h-8 bg-zinc-200" />
          <div>
            <div
              class="text-2xl sm:text-3xl font-black text-zinc-900 tabular-nums tracking-tight leading-tight"
            >
              {{ animatedCountries }}
            </div>
            <div
              class="text-[10px] text-zinc-400 font-semibold uppercase tracking-wider"
            >
              Countries
            </div>
          </div>
          <div
            class="ml-auto shrink-0 flex items-center gap-1 text-[9px] text-zinc-400 font-medium uppercase tracking-widest"
          >
            <span
              class="w-1.5 h-1.5 rounded-full bg-emerald-500 animate-pulse"
            />
            Live
          </div>
        </div>

        <!-- Bottom row: country flags with call counts (hidden on mobile) -->
        <div
          v-if="stats.countries.length > 0"
          class="hidden sm:flex flex-wrap gap-2 pt-3 border-t border-zinc-200"
        >
          <div
            v-for="country in stats.countries"
            :key="country.code"
            class="flex items-center gap-1.5 px-2 py-1 rounded-lg bg-white border border-zinc-100 shadow-sm"
            :title="`${country.name} · ${country.calls} calls`"
          >
            <img
              :src="`https://flagcdn.com/w40/${country.code.toLowerCase()}.png`"
              :alt="country.name"
              class="w-6 h-4 rounded-[3px] object-cover ring-1 ring-black/5"
            />
            <span class="text-xs font-bold text-zinc-700 tabular-nums">{{
              country.calls
            }}</span>
          </div>
        </div>
      </div>

      <!-- Stats loading skeleton -->
      <div
        v-else-if="statsLoading"
        class="col-span-1 sm:col-span-2 h-20 rounded-xl bg-zinc-100 animate-pulse"
      />
    </div>
  </div>
</template>
