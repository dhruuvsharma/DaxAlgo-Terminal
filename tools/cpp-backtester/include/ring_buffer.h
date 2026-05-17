#pragma once

#include <array>
#include <atomic>
#include <cassert>
#include <cstddef>
#include <optional>

namespace bt {

// Single-producer, single-consumer lock-free ring buffer
// N must be power of 2
template<typename T, std::size_t N>
class RingBuffer {
    static_assert((N & (N - 1)) == 0, "N must be power of 2");

public:
    bool push(const T& item) noexcept {
        const auto head = head_.load(std::memory_order_relaxed);
        const auto next = (head + 1) & mask_;
        if (next == tail_.load(std::memory_order_acquire))
            return false; // full
        buffer_[head] = item;
        head_.store(next, std::memory_order_release);
        return true;
    }

    bool push(T&& item) noexcept {
        const auto head = head_.load(std::memory_order_relaxed);
        const auto next = (head + 1) & mask_;
        if (next == tail_.load(std::memory_order_acquire))
            return false;
        buffer_[head] = std::move(item);
        head_.store(next, std::memory_order_release);
        return true;
    }

    std::optional<T> pop() noexcept {
        const auto tail = tail_.load(std::memory_order_relaxed);
        if (tail == head_.load(std::memory_order_acquire))
            return std::nullopt; // empty
        T item = buffer_[tail];
        tail_.store((tail + 1) & mask_, std::memory_order_release);
        return item;
    }

    [[nodiscard]] bool empty() const noexcept {
        return head_.load(std::memory_order_acquire) ==
               tail_.load(std::memory_order_acquire);
    }

    [[nodiscard]] std::size_t size() const noexcept {
        return (head_.load(std::memory_order_acquire) -
                tail_.load(std::memory_order_acquire)) & mask_;
    }

private:
    static constexpr std::size_t mask_ = N - 1;
    alignas(64) std::atomic<std::size_t> head_{0};
    alignas(64) std::atomic<std::size_t> tail_{0};
    std::array<T, N> buffer_{};
};

// Simple memory pool for zero-allocation order creation
template<typename T, std::size_t PoolSize = 65536>
class ObjectPool {
public:
    template<typename... Args>
    T* acquire(Args&&... args) {
        if (free_list_head_ < PoolSize) {
            T* obj = &pool_[free_list_head_++];
            new (obj) T(std::forward<Args>(args)...);
            return obj;
        }
        return nullptr; // pool exhausted
    }

    void release(T* obj) noexcept {
        obj->~T();
        if (free_list_head_ > 0) {
            pool_[--free_list_head_] = *obj;
        }
    }

    void reset() noexcept {
        free_list_head_ = 0;
    }

private:
    alignas(64) std::array<T, PoolSize> pool_{};
    std::size_t free_list_head_{0};
};

} // namespace bt
