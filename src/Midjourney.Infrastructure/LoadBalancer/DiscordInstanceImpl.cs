﻿using Midjourney.Infrastructure.Domain;
using Midjourney.Infrastructure.Result;
using Midjourney.Infrastructure.Services;
using Midjourney.Infrastructure.Util;
using Serilog;
using System.Collections.Concurrent;

namespace Midjourney.Infrastructure.LoadBalancer
{
    /// <summary>
    /// Discord实例实现类。
    /// 实现了IDiscordInstance接口，负责处理Discord相关的任务管理和执行。
    /// </summary>
    public class DiscordInstanceImpl : IDiscordInstance
    {
        private readonly DiscordAccount _account;
        private readonly IDiscordService _service;
        private readonly ITaskStoreService _taskStoreService;
        private readonly INotifyService _notifyService;
        private readonly ILogger _logger;
        private readonly List<TaskInfo> _runningTasks;
        private readonly ConcurrentDictionary<string, Task> _taskFutureMap;
        private readonly SemaphoreSlimLock _semaphoreSlimLock;

        private Task _loggingTask;
        private ManualResetEvent _mre; // 信号

        private ConcurrentQueue<(TaskInfo, Func<Task<Message>>)> _queueTasks;

        /// <summary>
        /// 初始化 DiscordInstanceImpl 类的新实例。
        /// </summary>
        /// <param name="account">Discord账号信息</param>
        /// <param name="service">Discord服务接口</param>
        /// <param name="taskStoreService">任务存储服务接口</param>
        /// <param name="notifyService">通知服务接口</param>
        public DiscordInstanceImpl(
            DiscordAccount account,
            IDiscordService service,
            ITaskStoreService taskStoreService,
            INotifyService notifyService)
        {
            _account = account;
            _service = service;
            _taskStoreService = taskStoreService;
            _notifyService = notifyService;

            _logger = Log.Logger;
            _runningTasks = new List<TaskInfo>();
            _queueTasks = new ConcurrentQueue<(TaskInfo, Func<Task<Message>>)>();
            _taskFutureMap = new ConcurrentDictionary<string, Task>();

            // 最小 1, 最大 12
            _semaphoreSlimLock = new SemaphoreSlimLock(Math.Max(1, Math.Min(account.CoreSize, 12)));

            _mre = new ManualResetEvent(false);

            // 后台任务
            _loggingTask = new Task(Running, TaskCreationOptions.LongRunning);
            _loggingTask.Start();
        }

        /// <summary>
        /// 获取实例ID。
        /// </summary>
        /// <returns>实例ID</returns>
        public string GetInstanceId() => _account.ChannelId;

        /// <summary>
        /// 获取Discord账号信息。
        /// </summary>
        /// <returns>Discord账号</returns>
        public DiscordAccount Account() => _account;

        /// <summary>
        /// 判断实例是否存活。
        /// </summary>
        /// <returns>是否存活</returns>
        public bool IsAlive() => _account.Enable;

        /// <summary>
        /// 获取正在运行的任务列表。
        /// </summary>
        /// <returns>正在运行的任务列表</returns>
        public List<TaskInfo> GetRunningTasks() => _runningTasks;

        /// <summary>
        /// 获取队列中的任务列表。
        /// </summary>
        /// <returns>队列中的任务列表</returns>
        public List<TaskInfo> GetQueueTasks() => new List<TaskInfo>(_queueTasks.Select(c => c.Item1));

        /// <summary>
        /// 后台服务执行任务
        /// </summary>
        private void Running()
        {
            while (true)
            {
                // 等待信号通知
                _mre.WaitOne();

                // 判断是否还有资源可用
                while (!_semaphoreSlimLock.TryWait(100))
                {
                    // 等待
                    Thread.Sleep(100);
                }

                // 允许同时执行 N 个信号量的任务
                while (_queueTasks.TryDequeue(out var info))
                {
                    // 判断是否还有资源可用
                    while (!_semaphoreSlimLock.TryWait(100))
                    {
                        // 等待
                        Thread.Sleep(100);
                    }

                    _taskFutureMap[info.Item1.Id] = ExecuteTaskAsync(info.Item1, info.Item2);
                }

                // 重新设置信号
                _mre.Reset();
            }
        }

        /// <summary>
        /// 退出任务并进行保存和通知。
        /// </summary>
        /// <param name="task">任务信息</param>
        public void ExitTask(TaskInfo task)
        {
            _taskFutureMap.TryRemove(task.Id, out _);
            SaveAndNotify(task);

            // 判断 _queueTasks 队列中是否存在指定任务，如果有则移除
            if (_queueTasks.Any(c => c.Item1.Id == task.Id))
            {
                _queueTasks = new ConcurrentQueue<(TaskInfo, Func<Task<Message>>)>(_queueTasks.Where(c => c.Item1.Id != task.Id));
            }
        }

        /// <summary>
        /// 获取正在运行的任务Future映射。
        /// </summary>
        /// <returns>任务Future映射</returns>
        public Dictionary<string, Task> GetRunningFutures() => new Dictionary<string, Task>(_taskFutureMap);

        /// <summary>
        /// 提交任务。
        /// </summary>
        /// <param name="info">任务信息</param>
        /// <param name="discordSubmit">Discord提交任务的委托</param>
        /// <returns>任务提交结果</returns>
        public SubmitResultVO SubmitTask(TaskInfo info, Func<Task<Message>> discordSubmit)
        {
            _taskStoreService.Save(info);

            int currentWaitNumbers = _queueTasks.Count;
            try
            {
                _queueTasks.Enqueue((info, discordSubmit));

                // 通知后台服务有新的任务
                _mre.Set();

                if (currentWaitNumbers == 0)
                {
                    return SubmitResultVO.Of(ReturnCode.SUCCESS, "提交成功", info.Id)
                        .SetProperty(Constants.TASK_PROPERTY_DISCORD_INSTANCE_ID, GetInstanceId());
                }
                else
                {
                    return SubmitResultVO.Of(ReturnCode.IN_QUEUE, $"排队中，前面还有{currentWaitNumbers}个任务", info.Id)
                        .SetProperty("numberOfQueues", currentWaitNumbers)
                        .SetProperty(Constants.TASK_PROPERTY_DISCORD_INSTANCE_ID, GetInstanceId());
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "submit task error");

                _taskStoreService.Delete(info.Id);

                return SubmitResultVO.Fail(ReturnCode.FAILURE, "提交失败，系统异常")
                    .SetProperty(Constants.TASK_PROPERTY_DISCORD_INSTANCE_ID, GetInstanceId());
            }
        }

        /// <summary>
        /// 异步执行任务。
        /// </summary>
        /// <param name="info">任务信息</param>
        /// <param name="discordSubmit">Discord提交任务的委托</param>
        /// <returns>异步任务</returns>
        private async Task ExecuteTaskAsync(TaskInfo info, Func<Task<Message>> discordSubmit)
        {
            _semaphoreSlimLock.Wait();
            _runningTasks.Add(info);

            try
            {
                var result = await discordSubmit();

                info.StartTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                if (result.Code != ReturnCode.SUCCESS)
                {
                    info.Fail(result.Description);
                    SaveAndNotify(info);
                    _logger.Debug("[{AccountDisplay}] task finished, id: {TaskId}, status: {TaskStatus}", _account.GetDisplay(), info.Id, info.Status);
                    return;
                }

                info.Status = TaskStatus.SUBMITTED;
                info.Progress = "0%";

                await Task.Delay(1000);

                await AsyncSaveAndNotify(info);

                while (info.Status == TaskStatus.SUBMITTED || info.Status == TaskStatus.IN_PROGRESS)
                {
                    await Task.Delay(1000);
                    await AsyncSaveAndNotify(info);
                }

                _logger.Debug("[{AccountDisplay}] task finished, id: {TaskId}, status: {TaskStatus}", _account.GetDisplay(), info.Id, info.Status);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[{AccountDisplay}] task execute error, id: {TaskId}", _account.GetDisplay(), info.Id);
                info.Fail("[Internal Server Error] " + ex.Message);

                SaveAndNotify(info);
            }
            finally
            {
                _runningTasks.Remove(info);
                _taskFutureMap.TryRemove(info.Id, out _);
                _semaphoreSlimLock.Release();
            }
        }

        /// <summary>
        /// 异步保存和通知任务。
        /// </summary>
        /// <param name="task">任务信息</param>
        /// <returns>异步任务</returns>
        private async Task AsyncSaveAndNotify(TaskInfo task) => await Task.Run(() => SaveAndNotify(task));

        /// <summary>
        /// 保存并通知任务状态变化。
        /// </summary>
        /// <param name="task">任务信息</param>
        private void SaveAndNotify(TaskInfo task)
        {
            _taskStoreService.Save(task);
            _notifyService.NotifyTaskChange(task);
        }

        /// <summary>
        /// 异步执行想象任务。
        /// </summary>
        /// <param name="prompt">提示信息</param>
        /// <param name="nonce">随机数</param>
        /// <returns>异步任务</returns>
        public Task<Message> ImagineAsync(string prompt, string nonce) => _service.ImagineAsync(prompt, nonce);

        /// <summary>
        /// 异步执行放大任务。
        /// </summary>
        /// <param name="messageId">消息ID</param>
        /// <param name="index">索引</param>
        /// <param name="messageHash">消息哈希</param>
        /// <param name="messageFlags">消息标志</param>
        /// <param name="nonce">随机数</param>
        /// <returns>异步任务</returns>
        public Task<Message> UpscaleAsync(string messageId, int index, string messageHash, int messageFlags, string nonce) => _service.UpscaleAsync(messageId, index, messageHash, messageFlags, nonce);

        /// <summary>
        /// 异步执行变体任务。
        /// </summary>
        /// <param name="messageId">消息ID</param>
        /// <param name="index">索引</param>
        /// <param name="messageHash">消息哈希</param>
        /// <param name="messageFlags">消息标志</param>
        /// <param name="nonce">随机数</param>
        /// <returns>异步任务</returns>
        public Task<Message> VariationAsync(string messageId, int index, string messageHash, int messageFlags, string nonce) => _service.VariationAsync(messageId, index, messageHash, messageFlags, nonce);

        /// <summary>
        /// 异步执行重新滚动任务。
        /// </summary>
        /// <param name="messageId">消息ID</param>
        /// <param name="messageHash">消息哈希</param>
        /// <param name="messageFlags">消息标志</param>
        /// <param name="nonce">随机数</param>
        /// <returns>异步任务</returns>
        public Task<Message> RerollAsync(string messageId, string messageHash, int messageFlags, string nonce) => _service.RerollAsync(messageId, messageHash, messageFlags, nonce);

        /// <summary>
        /// 执行动作
        /// </summary>
        /// <param name="messageId"></param>
        /// <param name="customId"></param>
        /// <param name="messageFlags"></param>
        /// <param name="nonce"></param>
        /// <returns></returns>
        public Task<Message> ActionAsync(string messageId, string customId, int messageFlags, string nonce) =>
              _service.ActionAsync(messageId, customId, messageFlags, nonce);

        /// <summary>
        /// 异步执行描述任务。
        /// </summary>
        /// <param name="finalFileName">最终文件名</param>
        /// <param name="nonce">随机数</param>
        /// <returns>异步任务</returns>
        public Task<Message> DescribeAsync(string finalFileName, string nonce) => _service.DescribeAsync(finalFileName, nonce);

        /// <summary>
        /// 异步执行混合任务。
        /// </summary>
        /// <param name="finalFileNames">最终文件名列表</param>
        /// <param name="dimensions">混合维度</param>
        /// <param name="nonce">随机数</param>
        /// <returns>异步任务</returns>
        public Task<Message> BlendAsync(List<string> finalFileNames, BlendDimensions dimensions, string nonce) => _service.BlendAsync(finalFileNames, dimensions, nonce);

        /// <summary>
        /// 异步上传文件。
        /// </summary>
        /// <param name="fileName">文件名</param>
        /// <param name="dataUrl">数据URL</param>
        /// <returns>异步任务</returns>
        public Task<Message> UploadAsync(string fileName, DataUrl dataUrl) => _service.UploadAsync(fileName, dataUrl);

        /// <summary>
        /// 异步发送图像消息。
        /// </summary>
        /// <param name="content">消息内容</param>
        /// <param name="finalFileName">最终文件名</param>
        /// <returns>异步任务</returns>
        public Task<Message> SendImageMessageAsync(string content, string finalFileName) => _service.SendImageMessageAsync(content, finalFileName);

        /// <summary>
        /// 查找符合条件的正在运行的任务。
        /// </summary>
        /// <param name="condition">条件</param>
        /// <returns>符合条件的正在运行的任务列表</returns>
        public IEnumerable<TaskInfo> FindRunningTask(Func<TaskInfo, bool> condition)
        {
            return GetRunningTasks().Where(condition);
        }

        /// <summary>
        /// 根据ID获取正在运行的任务。
        /// </summary>
        /// <param name="id">任务ID</param>
        /// <returns>任务信息</returns>
        public TaskInfo GetRunningTask(string id)
        {
            return GetRunningTasks().FirstOrDefault(t => id == t.Id);
        }

        /// <summary>
        /// 根据随机数获取正在运行的任务。
        /// </summary>
        /// <param name="nonce">随机数</param>
        /// <returns>任务信息</returns>
        public TaskInfo GetRunningTaskByNonce(string nonce)
        {
            if (string.IsNullOrWhiteSpace(nonce))
            {
                return null;
            }

            return FindRunningTask(c => c.Nonce == nonce).FirstOrDefault();
        }

        /// <summary>
        /// 根据消息ID获取正在运行的任务。
        /// </summary>
        /// <param name="messageId">消息ID</param>
        /// <returns>任务信息</returns>
        public TaskInfo GetRunningTaskByMessageId(string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId))
            {
                return null;
            }

            return FindRunningTask(c => c.MessageId == messageId).FirstOrDefault();
        }
    }
}