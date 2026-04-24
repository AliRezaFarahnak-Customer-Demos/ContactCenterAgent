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
    color: "petroleum",
  },
  {
    icon: "📞",
    title: "24/7 Support",
    desc: "Always-on, always consistent. Handle surges without hiring. No hold music, ever.",
    color: "sand",
  },
  {
    icon: "💼",
    title: "Sales Qualification",
    desc: "Qualify inbound leads with a call within 60 seconds of form submit. Instant engagement.",
    color: "petroleum",
  },
  {
    icon: "🏥",
    title: "Patient Scheduling",
    desc: "500 check-up calls in an hour. Confirmations, reminders, rescheduling — all handled.",
    color: "sand",
  },
  {
    icon: "🚨",
    title: "Emergency Alerts",
    desc: "Mass-notify thousands in minutes — evacuations, outages, weather warnings. Every second counts.",
    color: "red",
  },
  {
    icon: "💳",
    title: "Debt Collection",
    desc: "Automated, compliant collection calls at scale. Consistent tone, higher contact rates, lower costs.",
    color: "petroleum",
  },
];

const colorClasses: Record<
  string,
  { bg: string; border: string; icon: string }
> = {
  petroleum: {
    bg: "bg-norlys-petroleum/5",
    border: "border-norlys-petroleum/20 hover:border-norlys-petroleum/40",
    icon: "bg-norlys-petroleum/10",
  },
  sand: {
    bg: "bg-norlys-sand-2",
    border: "border-norlys-light-petroleum hover:border-norlys-petroleum/30",
    icon: "bg-norlys-sand-3",
  },
  red: {
    bg: "bg-norlys-red/5",
    border: "border-norlys-red/20 hover:border-norlys-red/40",
    icon: "bg-norlys-red/10",
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
        class="font-headline text-3xl sm:text-4xl font-bold text-norlys-petroleum-3 tracking-tight"
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
          aria-hidden="true"
        >
          {{ uc.icon }}
        </div>
        <div class="min-w-0">
          <div
            class="font-headline text-xs sm:text-sm font-bold text-norlys-petroleum-3"
          >
            {{ uc.title }}
          </div>
          <div
            class="hidden sm:block text-xs text-norlys-ink/70 leading-relaxed mt-0.5"
          >
            {{ uc.desc }}
          </div>
        </div>
      </div>

      <!-- Live stats card (full width, below the use cases) -->
      <div
        v-if="stats && (stats.totalCalls > 0 || stats.countries.length > 0)"
        class="col-span-2 p-4 rounded-xl border bg-norlys-sand-2 border-norlys-light-petroleum hover:border-norlys-petroleum/30 hover:shadow-lg transition-all duration-500 cursor-default"
        :class="
          visible ? 'opacity-100 translate-y-0' : 'opacity-0 translate-y-4'
        "
        :style="{ transitionDelay: '980ms' }"
      >
        <!-- Top row: stats numbers + live badge -->
        <div class="flex items-center gap-6 sm:mb-3">
          <div
            class="shrink-0 w-10 h-10 rounded-xl bg-norlys-light-petroleum flex items-center justify-center text-xl"
            aria-hidden="true"
          >
            📊
          </div>
          <div>
            <div
              class="font-headline text-2xl sm:text-3xl font-bold text-norlys-petroleum-3 tabular-nums tracking-tight leading-tight"
            >
              {{ animatedCalls.toLocaleString() }}
            </div>
            <div
              class="text-[10px] text-norlys-petroleum/60 font-semibold uppercase tracking-wider"
            >
              AI Calls
            </div>
          </div>
          <div class="w-px h-8 bg-norlys-light-petroleum-3" />
          <div>
            <div
              class="font-headline text-2xl sm:text-3xl font-bold text-norlys-petroleum-3 tabular-nums tracking-tight leading-tight"
            >
              {{ animatedCountries }}
            </div>
            <div
              class="text-[10px] text-norlys-petroleum/60 font-semibold uppercase tracking-wider"
            >
              Countries
            </div>
          </div>
          <div
            class="ml-auto shrink-0 flex items-center gap-1 text-[9px] text-norlys-petroleum/60 font-medium uppercase tracking-widest"
          >
            <span
              class="w-1.5 h-1.5 rounded-full bg-norlys-red animate-pulse"
            />
            Live
          </div>
        </div>

        <!-- Bottom row: country flags with call counts (hidden on mobile) -->
        <div
          v-if="stats.countries.length > 0"
          class="hidden sm:flex flex-wrap gap-2 pt-3 border-t border-norlys-light-petroleum"
        >
          <div
            v-for="country in stats.countries"
            :key="country.code"
            class="flex items-center gap-1.5 px-2 py-1 rounded-lg bg-white border border-norlys-light-petroleum shadow-sm"
            :title="`${country.name} · ${country.calls} calls`"
          >
            <img
              :src="`https://flagcdn.com/w40/${country.code.toLowerCase()}.png`"
              :alt="country.name"
              class="w-6 h-4 rounded-[3px] object-cover ring-1 ring-black/5"
            />
            <span
              class="text-xs font-bold text-norlys-petroleum-3 tabular-nums"
              >{{ country.calls }}</span
            >
          </div>
        </div>
      </div>

      <!-- Stats loading skeleton -->
      <div
        v-else-if="statsLoading"
        class="col-span-1 sm:col-span-2 h-20 rounded-xl bg-norlys-sand-2 animate-pulse"
      />
    </div>
  </div>
</template>
